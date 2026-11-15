using Humanizer;
using Microsoft.Data.SqlClient;
using NLog;
using NPoco;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlTypes;
using System.Linq;
using System.Text;

namespace SyncChanges
{
    /// <summary>
    /// Collects statements and sends them to the database in as few round trips as possible, keeping their order.
    /// A round trip holds at most <c>batchSize</c> statements.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The text of each statement with parameters, and the declarations of its parameters, are the same for all changes
    /// of one kind to one table, so SQL Server compiles its plan once and then reuses it. Compiling takes longer than
    /// executing these statements.
    /// </para>
    /// <para>
    /// On .NET 8 and later, the statements are sent as one RPC call each, several in one round trip (<c>SqlBatch</c>).
    /// Elsewhere, one command holds an <c>exec sp_executesql</c> for each statement, with the statement's text and
    /// declarations passed as parameters, so that they are not parsed for parameters on the way. SQL Server takes time
    /// for such a command that grows with the square of its number of parameters, which are limited to 2100 anyway, so this
    /// is several times slower when a round trip takes long.
    /// </para>
    /// </remarks>
    internal sealed class StatementBatch(CancellableDatabase db, int batchSize, bool dryRun)
    {
        static readonly Logger Log = LogManager.GetLogger(typeof(Synchronizer).FullName);

#if NET8_0_OR_GREATER
        private readonly List<SqlBatchCommand> Commands = [];
#else
        const int MaxParameters = 2000;

        private readonly StringBuilder Sql = new();
        private readonly List<object> Args = [];
        private readonly Dictionary<string, int> TextParameters = []; // by statement text or declaration: the parameter holding it
#endif
        private int Statements;
        private int FirstChange;
        private int LastChange;

        /// <summary>
        /// Gets or sets the number (1-based) of the change whose statements are being added. Used for error messages.
        /// </summary>
        public int CurrentChange { get; set; }

        /// <summary>
        /// Adds a statement without parameters, sending the statements collected so far first if it would not fit.
        /// </summary>
        public void Add(string sql)
        {
            Reserve(0);
#if NET8_0_OR_GREATER
            Commands.Add(new SqlBatchCommand(sql));
#else
            Sql.Append(sql).AppendLine(";");
#endif
        }

        /// <summary>
        /// Adds a statement, sending the statements collected so far first if it would not fit.
        /// </summary>
        /// <param name="sql">The statement's SQL, with parameters @0 to @n.</param>
        /// <param name="types">The types of the parameters, as they are declared (e.g. <c>nvarchar(200)</c>).</param>
        /// <param name="args">The statement's parameter values.</param>
        public void Add(string sql, IList<string> types, object[] args)
        {
#if NET8_0_OR_GREATER
            Reserve(args.Length);
            var command = new SqlBatchCommand(sql);
            for (int i = 0; i < args.Length; i++)
                command.Parameters.Add(CreateParameter("@" + i, types[i], args[i]));
            Commands.Add(command);
#else
            var declarations = string.Join(", ", types.Select((t, i) => $"@{i} {t}"));
            var newTexts = (TextParameters.ContainsKey(sql) ? 0 : 1) + (TextParameters.ContainsKey(declarations) ? 0 : 1);
            Reserve(args.Length + newTexts);

            var sqlParameter = TextParameter(sql);
            var declarationsParameter = TextParameter(declarations);

            Sql.Append($"exec sp_executesql @{sqlParameter}, @{declarationsParameter}");
            for (int i = 0; i < args.Length; i++)
            {
                Sql.Append($", @{Args.Count}");
                Args.Add(TypeValue(types[i], args[i]));
            }
            Sql.AppendLine(";");
#endif
        }

        // Makes room for a statement with the given number of new parameters, sending the batch first if needed.
        private void Reserve(int parameters)
        {
#if NET8_0_OR_GREATER
            var full = Statements >= batchSize;
#else
            var full = Statements >= batchSize || Args.Count + parameters > MaxParameters;
#endif
            if (Statements > 0 && full)
                Flush();

            if (Statements == 0)
                FirstChange = CurrentChange;

            Statements++;
            LastChange = CurrentChange;
        }

        /// <summary>
        /// Sends the statements collected so far.
        /// </summary>
        public void Flush()
        {
            if (Statements == 0) return;

            db.Token.ThrowIfCancellationRequested();

            try
            {
                if (!dryRun)
                {
#if NET8_0_OR_GREATER
                    using var batch = new SqlBatch((SqlConnection)db.Connection, (SqlTransaction)db.Transaction);
                    if (db.CommandTimeout > 0)
                        batch.Timeout = db.CommandTimeout;
                    foreach (var command in Commands)
                        batch.BatchCommands.Add(command);
                    using (db.CancelOnToken(batch.Cancel))
                        batch.ExecuteNonQuery();
#else
                    db.Execute(Sql.ToString(), [.. Args]);
#endif
                }
            }
            catch when (!db.Token.IsCancellationRequested)
            {
                var changes = FirstChange == LastChange ? $"change #{FirstChange}" : $"changes #{FirstChange} to #{LastChange}";
                Log.Error($"Error executing {"statement".ToQuantity(Statements)} for {changes}");
                throw;
            }
            finally
            {
#if NET8_0_OR_GREATER
                Commands.Clear();
#else
                Sql.Clear();
                Args.Clear();
                TextParameters.Clear();
#endif
                Statements = 0;
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Creates a parameter of the given declared type, so that its declaration is the same whatever the value,
        /// null included.
        /// </summary>
        internal static SqlParameter CreateParameter(string name, string type, object value)
        {
            var parameter = new SqlParameter(name, value ?? DBNull.Value);
            var open = type.IndexOf('(');
            var typeName = open < 0 ? type : type.Substring(0, open);
            string[] typeArgs = open < 0 ? [] : type.Substring(open + 1, type.Length - open - 2).Split(',').Select(a => a.Trim()).ToArray();
            int Size() => typeArgs.Length > 0 && typeArgs[0] != "max" ? int.Parse(typeArgs[0]) : -1;

            switch (typeName)
            {
                case "bigint": parameter.SqlDbType = SqlDbType.BigInt; break;
                case "binary": parameter.SqlDbType = SqlDbType.Binary; parameter.Size = Size(); break;
                case "bit": parameter.SqlDbType = SqlDbType.Bit; break;
                case "char": parameter.SqlDbType = SqlDbType.Char; parameter.Size = Size(); break;
                case "date": parameter.SqlDbType = SqlDbType.Date; break;
                case "datetime": parameter.SqlDbType = SqlDbType.DateTime; break;
                case "datetime2": parameter.SqlDbType = SqlDbType.DateTime2; parameter.Scale = byte.Parse(typeArgs[0]); break;
                case "datetimeoffset": parameter.SqlDbType = SqlDbType.DateTimeOffset; parameter.Scale = byte.Parse(typeArgs[0]); break;
                case "decimal":
                case "numeric":
                    parameter.SqlDbType = SqlDbType.Decimal;
                    parameter.Precision = byte.Parse(typeArgs[0]);
                    parameter.Scale = byte.Parse(typeArgs[1]);
                    break;
                case "float": parameter.SqlDbType = SqlDbType.Float; break;
                case "int": parameter.SqlDbType = SqlDbType.Int; break;
                case "money": parameter.SqlDbType = SqlDbType.Money; break;
                case "nchar": parameter.SqlDbType = SqlDbType.NChar; parameter.Size = Size(); break;
                case "nvarchar": parameter.SqlDbType = SqlDbType.NVarChar; parameter.Size = Size(); break;
                case "real": parameter.SqlDbType = SqlDbType.Real; break;
                case "smalldatetime": parameter.SqlDbType = SqlDbType.SmallDateTime; break;
                case "smallint": parameter.SqlDbType = SqlDbType.SmallInt; break;
                case "smallmoney": parameter.SqlDbType = SqlDbType.SmallMoney; break;
                case "sql_variant": parameter.SqlDbType = SqlDbType.Variant; break;
                case "time": parameter.SqlDbType = SqlDbType.Time; parameter.Scale = byte.Parse(typeArgs[0]); break;
                case "tinyint": parameter.SqlDbType = SqlDbType.TinyInt; break;
                case "uniqueidentifier": parameter.SqlDbType = SqlDbType.UniqueIdentifier; break;
                case "varbinary": parameter.SqlDbType = SqlDbType.VarBinary; parameter.Size = Size(); break;
                case "varchar": parameter.SqlDbType = SqlDbType.VarChar; parameter.Size = Size(); break;
                case "xml": parameter.SqlDbType = SqlDbType.Xml; break;
                default: break; // e.g. a CLR type: the type follows from the value
            }

            return parameter;
        }
#else
        private int TextParameter(string text)
        {
            if (!TextParameters.TryGetValue(text, out var parameter))
            {
                parameter = Args.Count;
                TextParameters[text] = parameter;
                Args.Add(text);
            }

            return parameter;
        }

        // Gives a value a SQL type where the one derived from its .NET type does not fit the column: a null without
        // a type is sent as nvarchar, which SQL Server does not convert to binary types, and a DateTime is sent as
        // datetime, which would cut off the precision of a datetime2 column.
        private static object TypeValue(string type, object value)
        {
            if (value == DBNull.Value && (type.StartsWith("binary") || type.StartsWith("varbinary")))
                return SqlBinary.Null;
            if (type.StartsWith("datetime2"))
                return new SqlParameter { SqlDbType = SqlDbType.DateTime2, Value = value };
            return value;
        }
#endif
    }
}
