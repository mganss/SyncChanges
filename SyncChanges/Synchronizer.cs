using Humanizer;
using NLog;
using NPoco;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;

namespace SyncChanges
{
    /// <summary>
    /// Allows replication of database changes from a source database to one or more destination databases.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="Synchronizer"/> class.
    /// </remarks>
    /// <param name="config">The configuration.</param>
    /// <exception cref="System.ArgumentException"><paramref name="config"/> is null</exception>
    public class Synchronizer(Config config)
    {
        /// <summary>
        /// Gets or sets a value indicating whether a replication run only tests, without modifying destination databases.
        /// Default is the configuration's <see cref="Config.DryRun"/>, or <c>false</c>.
        /// </summary>
        /// <value>
        ///   <c>true</c> if destination databases will not be modified; otherwise, <c>false</c>.
        /// </value>
        public bool DryRun { get; set; } = config?.DryRun ?? false;

        /// <summary>
        /// Gets or sets the database command timeout in seconds. 0 leaves the default of the database client (30 seconds).
        /// Default is the configuration's <see cref="Config.Timeout"/>, or 0.
        /// </summary>
        /// <value>
        /// The database command timeout in seconds.
        /// </value>
        public int Timeout { get; set; } = config?.Timeout ?? 0;

        /// <summary>
        /// Gets or sets the minimum synchronization time interval in seconds.
        /// Default is the configuration's <see cref="Config.Interval"/>, or 30 seconds.
        /// </summary>
        /// <value>
        /// The synchronization time interval in seconds.
        /// </value>
        public int Interval { get; set; } = config?.Interval ?? 30;

        /// <summary>
        /// Gets or sets the maximum number of statements sent to a destination database in one round trip.
        /// Values less than 2 send each statement in its own round trip.
        /// Default is the configuration's <see cref="Config.BatchSize"/>, or 100.
        /// </summary>
        /// <value>
        /// The maximum number of statements per round trip.
        /// </value>
        public int BatchSize { get; set; } = config?.BatchSize ?? 100;

        /// <summary>
        /// Occurs when synchronization from a synchronization loop has succeeded.
        /// </summary>
        public event EventHandler<SyncEventArgs> Synced;

        static readonly Logger Log = LogManager.GetCurrentClassLogger();
        Config Config { get; set; } = config ?? throw new ArgumentException("config is null", nameof(config));
        bool Error { get; set; }
        CancellationToken Token { get; set; }

        // by the index of the replication set; null for a set that has not been initialized yet
        private readonly IList<TableInfo>[] Tables = new IList<TableInfo>[config.ReplicationSets.Count];

        /// <summary>
        /// Initialize the synchronization process.
        /// </summary>
        public void Init()
        {
            for (int i = 0; i < Config.ReplicationSets.Count; i++)
            {
                Init(i);
            }
        }

        private void Init(int index)
        {
            if (index < 0 || index >= Config.ReplicationSets.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");

            if (Tables[index] != null)
                return;

            var replicationSet = Config.ReplicationSets[index];

            Log.Info($"Getting replication information for replication set {replicationSet.Name}");

            var tables = GetTables(replicationSet.Source);
            if (replicationSet.Tables != null && replicationSet.Tables.Any())
                tables = [.. tables.Select(t => new { Table = t, Name = t.Name.Replace("[", "").Replace("]", "") })
                    .Where(t => replicationSet.Tables.Exists(r => r == t.Name || r == t.Name.Split('.')[1]))
                    .Select(t => t.Table)];

            if (!tables.Any())
                Log.Warn("No tables to replicate (check if change tracking is enabled)");
            else
                Log.Info($"Replicating {"table".ToQuantity(tables.Count, ShowQuantityAs.None)} {string.Join(", ", tables.Select(t => t.Name))}");

            Tables[index] = tables;
        }

        /// <summary>
        /// Perform the synchronization.
        /// </summary>
        /// <returns>true, if the synchronization was successful; otherwise, false.</returns>
        public bool Sync() => Sync(CancellationToken.None);

        /// <summary>
        /// Perform the synchronization.
        /// </summary>
        /// <param name="token">The cancellation token. Cancelling it cancels the running database command, and rolls back
        /// the changes of the destination being replicated to.</param>
        /// <returns>true, if the synchronization was successful; otherwise, false.</returns>
        /// <exception cref="OperationCanceledException">The synchronization was cancelled.</exception>
        public bool Sync(CancellationToken token)
        {
            Error = false;
            Token = token;

            try
            {
                SyncAll();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && token.IsCancellationRequested)
            {
                // a cancelled command fails with the error of the database client
                throw new OperationCanceledException("Replication was cancelled.", ex, token);
            }

            Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

            return !Error;
        }

        private void SyncAll()
        {
            LogSettings();

            Init();

            for (int i = 0; i < Config.ReplicationSets.Count; i++)
            {
                var replicationSet = Config.ReplicationSets[i];
                var tables = Tables[i];

                Sync(replicationSet, tables);
            }
        }

        private bool Sync(ReplicationSet replicationSet, IList<TableInfo> tables, long sourceVersion = -1)
        {
            if (!tables.Any()) return true;

            // The result is for this replication set only, but the errors of earlier replication sets are kept.
            var previousError = Error;
            Error = false;

            Log.Info($"Starting replication for replication set {replicationSet.Name}");

            var destinationsByVersion = replicationSet.Destinations.GroupBy(d => GetCurrentVersion(d))
                .Where(d => d.Key >= 0 && (sourceVersion < 0 || d.Key < sourceVersion)).ToList();

            foreach (var destinations in destinationsByVersion)
                Replicate(replicationSet.Source, destinations, tables);

            var success = !Error;
            Error |= previousError;

            return success;
        }

        /// <summary>
        /// Performs synchronization in an infinite loop. Periodically checks if source version has increased to trigger replication.
        /// </summary>
        /// <param name="token">The cancellation token.</param>
        public void SyncLoop(CancellationToken token)
        {
            var currentVersions = Enumerable.Repeat(0L, Config.ReplicationSets.Count).ToList();
            Token = token;

            LogSettings();

            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    Log.Info("Stopping replication.");
                    return;
                }

                var start = DateTime.UtcNow;
                Error = false;

                for (int i = 0; i < Config.ReplicationSets.Count; i++)
                {
                    var replicationSet = Config.ReplicationSets[i];
                    var currentVersion = currentVersions[i];
                    long version = 0;

                    try
                    {
                        Init(i);

                        using (var db = GetDatabase(replicationSet.Source.ConnectionString, DatabaseType.SqlServer2008))
                            version = db.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");

                        Log.Debug($"Current version of source in replication set {replicationSet.Name} is {version}.");

                        if (version > currentVersion)
                        {
                            Log.Info($"Current version of source in replication set {replicationSet.Name} has increased from {currentVersion} to {version}: Starting replication.");

                            var tables = Tables[i];
                            var success = Sync(replicationSet, tables, version);

                            if (success) currentVersions[i] = version;

                            Synced?.Invoke(this, new SyncEventArgs { ReplicationSet = replicationSet, Version = version });
                        }
                    }
                    catch (Exception) when (token.IsCancellationRequested)
                    {
                        // a cancelled command fails with the error of the database client, the changes to the
                        // destination are rolled back
                        Log.Info($"Cancelled replication of set {replicationSet.Name}.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error occurred during replication of set {replicationSet.Name}.");
                        Error = true;
                    }

                    if (token.IsCancellationRequested)
                    {
                        Log.Info("Stopping replication.");
                        return;
                    }
                }

                Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

                var delay = (int)Math.Round(Math.Max(0, (TimeSpan.FromSeconds(Interval) - (DateTime.UtcNow - start)).TotalSeconds) * 1000, MidpointRounding.AwayFromZero);
                token.WaitHandle.WaitOne(delay);
            }
        }

        private void LogSettings()
        {
            if (Timeout != 0)
                Log.Info($"Command timeout is {"second".ToQuantity(Timeout)}");

            Log.Info($"Batch size is {"statement".ToQuantity(Math.Max(1, BatchSize))}");
        }

        private IList<TableInfo> GetTables(DatabaseInfo dbInfo)
        {
            try
            {
                using var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2008);
                // ColumnType is the column's type as a parameter declaration of sp_executesql, e.g. nvarchar(200)
                var sql = @"select TableName, ColumnName, coalesce(max(cast(is_primary_key as tinyint)), 0) PrimaryKey,
                        coalesce(max(cast(is_identity as tinyint)), 0) IsIdentity, max(ColumnType) ColumnType from
                        (
                        select ('[' + s.name + '].[' + t.name + ']') TableName, ('[' + COL_NAME(t.object_id, a.column_id) + ']') ColumnName,
                        i.is_primary_key, a.is_identity,
                        case
                            when ty.name in ('varchar', 'char', 'varbinary', 'binary')
                                then ty.name + '(' + case when a.max_length = -1 then 'max' else cast(a.max_length as varchar(10)) end + ')'
                            when ty.name in ('nvarchar', 'nchar')
                                then ty.name + '(' + case when a.max_length = -1 then 'max' else cast(a.max_length / 2 as varchar(10)) end + ')'
                            when ty.name in ('decimal', 'numeric')
                                then ty.name + '(' + cast(a.precision as varchar(10)) + ', ' + cast(a.scale as varchar(10)) + ')'
                            when ty.name in ('datetime2', 'time', 'datetimeoffset')
                                then ty.name + '(' + cast(a.scale as varchar(10)) + ')'
                            when ty.name = 'text' then 'varchar(max)'
                            when ty.name = 'ntext' then 'nvarchar(max)'
                            when ty.name = 'image' then 'varbinary(max)'
                            else ty.name
                        end ColumnType
                        from sys.change_tracking_tables tr
                        join sys.tables t on t.object_id = tr.object_id
                        join sys.schemas s on s.schema_id = t.schema_id
                        join sys.columns a on a.object_id = t.object_id
                        join sys.types ty on ty.user_type_id = case when a.system_type_id = 240 then a.user_type_id else a.system_type_id end
                        left join sys.index_columns c on c.object_id = t.object_id and c.column_id = a.column_id
                        left join sys.indexes i on i.object_id = t.object_id and i.index_id = c.index_id
                        -- the destination generates the values of these columns itself and refuses explicit ones:
                        -- rowversion (timestamp), and GENERATED ALWAYS columns such as the period columns of temporal tables
                        -- (the property is null before SQL Server 2016, which has no such columns)
                        where a.is_computed = 0 and a.system_type_id <> 189
                            and coalesce(columnproperty(a.object_id, a.name, 'GeneratedAlwaysType'), 0) = 0
                        ) X
                        group by TableName, ColumnName
                        order by TableName, ColumnName";

                var tables = db.Fetch<dynamic>(sql).GroupBy(t => t.TableName)
                    .Select(g => new TableInfo
                    {
                        Name = (string)g.Key,
                        KeyColumns = [.. g.Where(c => (int)c.PrimaryKey > 0).Select(c => (string)c.ColumnName)],
                        OtherColumns = [.. g.Where(c => (int)c.PrimaryKey == 0).Select(c => (string)c.ColumnName)],
                        HasIdentity = g.Any(c => (int)c.IsIdentity > 0),
                        ColumnTypes = g.ToDictionary(c => (string)c.ColumnName, c => (string)c.ColumnType)
                    }).ToList();

                var fks = db.Fetch<ForeignKeyConstraint>(@"select obj.name AS ForeignKeyName,
                            ('[' + sch.name + '].[' + tab1.name + ']') TableName,
                            ('[' +  col1.name + ']') ColumnName,
                            ('[' + sch2.name + '].[' + tab2.name + ']') ReferencedTableName,
                            ('[' +  col2.name + ']') ReferencedColumnName
                        from sys.foreign_key_columns fkc
                        inner join sys.foreign_keys obj
                            on obj.object_id = fkc.constraint_object_id
                        inner join sys.tables tab1
                            on tab1.object_id = fkc.parent_object_id
                        inner join sys.schemas sch
                            on tab1.schema_id = sch.schema_id
                        inner join sys.columns col1
                            on col1.column_id = parent_column_id AND col1.object_id = tab1.object_id
                        inner join sys.tables tab2
                            on tab2.object_id = fkc.referenced_object_id
                        inner join sys.schemas sch2
                            on tab2.schema_id = sch2.schema_id
                        inner join sys.columns col2
                            on col2.column_id = referenced_column_id AND col2.object_id = tab2.object_id
                        where obj.is_disabled = 0");

                foreach (var table in tables)
                    table.ForeignKeyConstraints = [.. fks.Where(f => f.TableName == table.Name)];

                var uqcs = db.Fetch<UniqueColumn>(@"SELECT
                             IndexId = ('[' + sch.name + '].[' + t.name + '].[' + ind.name + ']'),
                             TableName = ('[' + sch.name + '].[' + t.name + ']'),
                             IndexName = ind.name,
                             ColumnName = ('[' +  col.name + ']'),
                             IsConstraint = is_unique_constraint
                        FROM
                             sys.indexes ind
                        INNER JOIN
                             sys.index_columns ic ON  ind.object_id = ic.object_id and ind.index_id = ic.index_id
                        INNER JOIN
                             sys.columns col ON ic.object_id = col.object_id and ic.column_id = col.column_id
                        INNER JOIN
                             sys.tables t ON ind.object_id = t.object_id
                        inner join sys.schemas sch
                            on t.schema_id = sch.schema_id
                        WHERE
                             ind.is_primary_key = 0
                             AND (ind.is_unique = 1 or ind.is_unique_constraint = 1)
                             AND t.is_ms_shipped = 0
                        ORDER BY
                             t.name, ind.name, ind.index_id, ic.is_included_column, ic.key_ordinal;");

                var uqs = uqcs.GroupBy(c => $"{c.TableName}_{c.IndexName}").Select(g => new UniqueConstraint
                {
                    IndexName = g.First().IndexName,
                    TableName = g.First().TableName,
                    IsConstraint = g.First().IsConstraint,
                    ColumnNames = [.. g.Select(c => c.ColumnName)],
                }).ToList();

                foreach (var table in tables)
                    table.UniqueConstraints = [.. uqs.Where(u => u.TableName == table.Name)];

                return tables;
            }
            catch (Exception ex) when (!Token.IsCancellationRequested)
            {
                Log.Fatal(ex, "Error getting tables to replicate from source database");
                throw;
            }
        }

        private CancellableDatabase GetDatabase(string connectionString, DatabaseType databaseType = null)
        {
            var db = new CancellableDatabase(connectionString, databaseType ?? DatabaseType.SqlServer2005, Microsoft.Data.SqlClient.SqlClientFactory.Instance, Token);

            if (Timeout != 0) db.CommandTimeout = Timeout;

            return db;
        }

        private void Replicate(DatabaseInfo source, IGrouping<long, DatabaseInfo> destinations, IList<TableInfo> tables)
        {
            var changeInfo = RetrieveChanges(source, destinations, tables);
            if (changeInfo == null) return;

            // replicate changes to destinations
            foreach (var destination in destinations)
            {
                Token.ThrowIfCancellationRequested();

                try
                {
                    Log.Info($"Replicating {"change".ToQuantity(changeInfo.Changes.Count)} to destination {destination.Name}");

                    using var db = GetDatabase(destination.ConnectionString, DatabaseType.SqlServer2005);
                    using var transaction = db.GetTransaction(System.Data.IsolationLevel.ReadUncommitted);

                    try
                    {
                        var changes = changeInfo.Changes;
                        var disabledForeignKeyConstraints = new List<ForeignKeyConstraint>();
                        var batch = new StatementBatch(db, BatchSize, DryRun);
                        var progress = new Progress(changes.Count);

                        for (int i = 0; i < changes.Count; i++)
                        {
                            var change = changes[i];
                            Log.Debug($"Replicating change #{i + 1} of {changes.Count} (Version {change.Version}, CreationVersion {change.CreationVersion})");

                            batch.CurrentChange = i + 1;

                            foreach (var fk in change.ForeignKeyConstraintsToDisable)
                            {
                                if (!disabledForeignKeyConstraints.Contains(fk))
                                {
                                    DisableForeignKeyConstraint(batch, fk);
                                    disabledForeignKeyConstraints.Add(fk);
                                }
                            }

                            PerformChange(batch, change);

                            if (progress.Due(i + 1, out var report))
                                Log.Info($"Replicated {i + 1} of {"change".ToQuantity(changes.Count)} to destination {destination.Name} {report}");
                        }

                        // Re-enabling checks the whole table, so it is done once, when all changes are in. The constraints
                        // were disabled in this transaction, so they are only ever off for its own statements.
                        foreach (var fk in disabledForeignKeyConstraints)
                            ReenableForeignKeyConstraint(batch, fk);

                        batch.Flush();

                        if (!DryRun)
                        {
                            SetSyncVersion(db, changeInfo.Version);
                            transaction.Complete();
                        }

                        Log.Info($"Destination {destination.Name} now at version {changeInfo.Version} (took {progress.Elapsed.Humanize(2)})");
                    }
                    catch (Exception ex) when (!Token.IsCancellationRequested)
                    {
                        Error = true;
                        Log.Error(ex, $"Error replicating changes to destination {destination.Name}");
                    }
                }
                catch (Exception ex) when (!Token.IsCancellationRequested)
                {
                    Error = true;
                    Log.Error(ex, $"Error replicating changes to destination {destination.Name}");
                }
            }
        }

        private static void ReenableForeignKeyConstraint(StatementBatch batch, ForeignKeyConstraint fk)
        {
            Log.Debug($"Re-enabling foreign key constraint {fk.ForeignKeyName}");
            batch.Add($"alter table {fk.TableName} with check check constraint [{fk.ForeignKeyName}]");
        }

        private static void DisableForeignKeyConstraint(StatementBatch batch, ForeignKeyConstraint fk)
        {
            Log.Debug($"Disabling foreign key constraint {fk.ForeignKeyName}");
            batch.Add($"alter table {fk.TableName} nocheck constraint [{fk.ForeignKeyName}]");
        }

        private void SetSyncVersion(Database db, long currentVersion)
        {
            if (!DryRun)
            {
                var syncInfoTableExists = db.ExecuteScalar<string>("select top(1) name from sys.tables where name ='SyncInfo'") != null;

                if (!syncInfoTableExists)
                {
                    db.Execute("create table SyncInfo (Id int not null primary key default 1 check (Id = 1), Version bigint not null)");
                    db.Execute("insert into SyncInfo (Version) values (@0)", currentVersion);
                }
                else
                {
                    db.Execute("update SyncInfo set Version = @0", currentVersion);
                }
            }
        }

        private ChangeInfo RetrieveChanges(DatabaseInfo source, IGrouping<long, DatabaseInfo> destinations, IList<TableInfo> tables)
        {
            var destinationVersion = destinations.Key;
            var changeInfo = new ChangeInfo();
            var changes = new List<Change>();

            using (var db = GetDatabase(source.ConnectionString, DatabaseType.SqlServer2008))
            {
                var snapshotIsolationEnabled = db.ExecuteScalar<int>("select snapshot_isolation_state from sys.databases where name = DB_NAME()") == 1;
                if (snapshotIsolationEnabled)
                {
                    Log.Info($"Snapshot isolation is enabled in database {source.Name}");
                    db.BeginTransaction(System.Data.IsolationLevel.Snapshot);
                }
                else
                    Log.Info($"Snapshot isolation is not enabled in database {source.Name}, ignoring all changes above current version");

                changeInfo.Version = db.ExecuteScalar<long>("select CHANGE_TRACKING_CURRENT_VERSION()");
                Log.Info($"Current version of database {source.Name} is {changeInfo.Version}");

                foreach (var table in tables)
                {
                    Token.ThrowIfCancellationRequested();

                    var tableName = table.Name;
                    var minVersion = db.ExecuteScalar<long?>("select CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@0))", tableName);

                    Log.Info($"Minimum version of table {tableName} in database {source.Name} is {minVersion}");

                    if (minVersion > destinationVersion)
                    {
                        Log.Error($"Cannot replicate table {tableName} to {"destination".ToQuantity(destinations.Count(), ShowQuantityAs.None)} {string.Join(", ", destinations.Select(d => d.Name))} because minimum source version {minVersion} is greater than destination version {destinationVersion}");
                        Error = true;
                        return null;
                    }

                    var sql = $@"select c.SYS_CHANGE_OPERATION, c.SYS_CHANGE_VERSION, c.SYS_CHANGE_CREATION_VERSION,
                        {string.Join(", ", table.KeyColumns.Select(c => "c." + c).Concat(table.OtherColumns.Select(c => "t." + c)))}
                        from CHANGETABLE (CHANGES {tableName}, @0) c
                        left outer join {tableName} t on ";
                    sql += string.Join(" and ", table.KeyColumns.Select(k => $"c.{k} = t.{k}"));
                    sql += " order by coalesce(c.SYS_CHANGE_CREATION_VERSION, c.SYS_CHANGE_VERSION), c.SYS_CHANGE_OPERATION";

                    Log.Debug($"Retrieving changes for table {tableName}: {sql}");

                    db.OpenSharedConnection();
                    var cmd = db.CreateCommand(db.Connection, System.Data.CommandType.Text, sql, destinationVersion);

                    // the registration also cancels the command while its results are read
                    using var registration = db.CancelOnToken(cmd.Cancel);
                    using var reader = cmd.ExecuteReader();
                    var numChanges = 0;

                    object[] nullableMap = null;

                    while (reader.Read())
                    {
                        nullableMap ??= reader.GetNullableTypeMap();

                        var col = 0;
                        var change = new Change { Operation = ((string)reader[col])[0], Table = table };
                        col++;
                        var version = reader.GetInt64(col);
                        change.Version = version;
                        col++;
                        var creationVersion = reader.IsDBNull(col) ? version : reader.GetInt64(col);
                        change.CreationVersion = creationVersion;
                        col++;

                        if (!snapshotIsolationEnabled && Math.Min(version, creationVersion) > changeInfo.Version)
                        {
                            Log.Warn($"Ignoring change version {Math.Min(version, creationVersion)}");
                            continue;
                        }

                        for (int i = 0; i < table.KeyColumns.Count; i++, col++)
                        {
                            var val = reader.GetValue(col);
                            change.Keys[table.KeyColumns[i]] = (val == DBNull.Value ? (nullableMap[col] ?? val) : val);
                        }

                        for (int i = 0; i < table.OtherColumns.Count; i++, col++)
                        {
                            var val = reader.GetValue(col);
                            change.Others[table.OtherColumns[i]] = (val == DBNull.Value ? (nullableMap[col] ?? val) : val);

                        }

                        changes.Add(change);
                        numChanges++;
                    }

                    Log.Info($"Table {tableName} has {"change".ToQuantity(numChanges)}");
                }

                if (snapshotIsolationEnabled)
                    db.CompleteTransaction();
            }

            changeInfo.Changes.AddRange(OrderForForeignKeys([.. changes.OrderBy(c => c.Version).ThenBy(c => c.Table.Name)]));

            return changeInfo;
        }

        /// <summary>
        /// Orders the changes so that a row is inserted before the rows that reference it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Changes come in the order of the version of their last change, and an insert carries the row's values
        /// from its last update. So a row that was inserted, then referenced by new rows, then updated, is inserted
        /// after the rows that reference it. Such an insert of a referencing row is deferred until just after the
        /// insert of the row it references, which needs no foreign key constraint to be disabled. Disabling one
        /// takes a schema modification lock on the table until the transaction ends, which blocks all readers of
        /// the destination table, even those reading uncommitted data.
        /// </para>
        /// <para>
        /// Deferring an insert cannot violate a unique constraint that the original order does not violate: until
        /// the row is inserted it holds no values, and the rows changed in between then hold their final values,
        /// which agree with the row's final values as they do in the source database.
        /// </para>
        /// <para>
        /// An update is not deferred, as its row keeps its old values in the meantime, which other changes may
        /// rely on having changed. An update referencing a row inserted later, and inserts referencing each other
        /// in a cycle, disable the foreign key constraint instead; it is re-enabled once all changes are in.
        /// </para>
        /// </remarks>
        private static List<Change> OrderForForeignKeys(List<Change> changes)
        {
            var referencedInserts = BuildReferencedInsertIndex(changes);
            var done = new bool[changes.Count];
            var ordered = new List<Change>(changes.Count);
            var waiting = new Dictionary<int, List<int>>(); // by the position of an insert: the deferred inserts waiting for it
            var released = new Queue<int>();
            var deferred = new HashSet<int>();
            var disabled = new Dictionary<ForeignKeyConstraint, int>();

            // The positions of the inserts of the row that the change at position i references through fk,
            // if none of them has been replicated yet, otherwise null.
            List<int> PendingInserts(int i, ForeignKeyConstraint fk)
            {
                var val = changes[i].GetValue(fk.ColumnName);
                if (val == null
                    || !referencedInserts.TryGetValue((fk.ReferencedTableName, fk.ReferencedColumnName), out var insertsByValue)
                    || !insertsByValue.TryGetValue(val, out var positions)
                    || positions.Any(j => j == i || done[j])) // a row may reference itself
                    return null;
                return positions;
            }

            void Add(int i)
            {
                ordered.Add(changes[i]);
                done[i] = true;

                if (waiting.TryGetValue(i, out var waiters))
                {
                    waiting.Remove(i);
                    foreach (var w in waiters)
                        released.Enqueue(w);
                }
            }

            void AddDisablingForeignKeys(int i)
            {
                var change = changes[i];

                foreach (var fk in change.Table.ForeignKeyConstraints.Where(f => PendingInserts(i, f) != null))
                {
                    if (!change.ForeignKeyConstraintsToDisable.Add(fk)) continue;
                    Log.Debug($"Foreign key constraint {fk.ForeignKeyName} needs to be disabled for change of table {change.Table.Name} (Version {change.Version}, CreationVersion {change.CreationVersion})");
                    disabled[fk] = disabled.TryGetValue(fk, out var count) ? count + 1 : 1;
                }

                Add(i);
            }

            void Schedule(int i)
            {
                if (done[i]) return;

                var change = changes[i];
                var pending = change.Table.ForeignKeyConstraints
                    .Select(f => (ForeignKey: f, Positions: PendingInserts(i, f)))
                    .FirstOrDefault(p => p.Positions != null);

                if (pending.Positions == null)
                    Add(i);
                else if (change.Operation == 'I')
                {
                    // wait for the first insert of the referenced row; the others are checked again when it is released
                    var position = pending.Positions[0];
                    Log.Debug($"Deferring insert into {change.Table.Name} (Version {change.Version}) until the row it references through {pending.ForeignKey.ForeignKeyName} is inserted");
                    if (!waiting.TryGetValue(position, out var waiters))
                        waiting[position] = waiters = [];
                    waiters.Add(i);
                    deferred.Add(i);
                }
                else
                    AddDisablingForeignKeys(i);
            }

            void ScheduleReleased()
            {
                while (released.Count > 0)
                    Schedule(released.Dequeue());
            }

            for (int i = 0; i < changes.Count; i++)
            {
                Schedule(i);
                ScheduleReleased();
            }

            // what is still waiting references rows in a cycle
            for (int i = 0; i < changes.Count; i++)
            {
                if (done[i]) continue;
                AddDisablingForeignKeys(i);
                ScheduleReleased();
            }

            if (deferred.Count > 0)
                Log.Info($"Deferred {"insert".ToQuantity(deferred.Count)} until the rows they reference are inserted");

            foreach (var fk in disabled)
                Log.Info($"Foreign key constraint {fk.Key.ForeignKeyName} needs to be disabled for {"change".ToQuantity(fk.Value)}");

            return ordered;
        }

        private static Dictionary<(string Table, string Column), Dictionary<object, List<int>>> BuildReferencedInsertIndex(List<Change> changes)
        {
            var referencedColumns = changes.Select(c => c.Table).Distinct()
                .SelectMany(t => t.ForeignKeyConstraints)
                .Select(f => (Table: f.ReferencedTableName, Column: f.ReferencedColumnName))
                .Distinct()
                .ToLookup(c => c.Table, c => c.Column);
            var index = new Dictionary<(string Table, string Column), Dictionary<object, List<int>>>();

            for (int j = 0; j < changes.Count; j++)
            {
                var change = changes[j];
                if (change.Operation != 'I') continue;

                foreach (var column in referencedColumns[change.Table.Name])
                {
                    var refVal = change.GetValue(column);
                    if (refVal == null) continue;

                    if (!index.TryGetValue((change.Table.Name, column), out var insertsByValue))
                        index[(change.Table.Name, column)] = insertsByValue = [];

                    if (!insertsByValue.TryGetValue(refVal, out var positions))
                        insertsByValue[refVal] = positions = [];

                    positions.Add(j);
                }
            }

            return index;
        }

        private static void PerformChange(StatementBatch batch, Change change)
        {
            var table = change.Table;
            var tableName = table.Name;

            // The statements number their parameters from @0 in the order of their columns (keys first), so the text of
            // a statement and its parameter declarations is the same for all changes of one kind to a table.
            switch (change.Operation)
            {
                // Insert
                case 'I':
                    var insertColumnNames = change.GetColumnNames();
                    var insertValues = change.GetValues();
                    var insertSql = string.Format("insert into {0} ({1}) values ({2})", tableName,
                        string.Join(", ", insertColumnNames),
                        string.Join(", ", insertColumnNames.Select((c, i) => "@" + i)));
                    if (table.HasIdentity)
                        insertSql = $"set IDENTITY_INSERT {tableName} ON; {insertSql}; set IDENTITY_INSERT {tableName} OFF";
                    if (Log.IsDebugEnabled)
                        Log.Debug($"Executing insert: {insertSql} ({FormatArgs(insertValues)})");
                    batch.Add(insertSql, ColumnTypes(table, insertColumnNames), insertValues);
                    break;

                // Update
                case 'U':
                    var updateColumnNames = change.Others.Keys.ToList();
                    // a table with only key columns has nothing to update: a changed key is tracked as a delete and an insert
                    if (updateColumnNames.Count == 0)
                    {
                        Log.Debug($"Skipping update of {tableName}, which has no columns besides its key");
                        break;
                    }
                    var updateValues = change.GetValues();
                    var updateSql = string.Format("update {0} set {1} where {2}", tableName,
                        string.Join(", ", updateColumnNames.Select((c, i) => $"{c} = @{i + change.Keys.Count}")),
                        PrimaryKeys(change));
                    if (Log.IsDebugEnabled)
                        Log.Debug($"Executing update: {updateSql} ({FormatArgs(updateValues)})");
                    batch.Add(updateSql, ColumnTypes(table, change.GetColumnNames()), updateValues);
                    break;

                // Delete
                case 'D':
                    var deleteValues = change.Keys.Values.ToArray();
                    var deleteSql = string.Format("delete from {0} where {1}", tableName, PrimaryKeys(change));
                    if (Log.IsDebugEnabled)
                        Log.Debug($"Executing delete: {deleteSql} ({FormatArgs(deleteValues)})");
                    batch.Add(deleteSql, ColumnTypes(table, change.Keys.Keys), deleteValues);
                    break;
            }
        }

        private static string FormatArgs(object[] args) => string.Join(", ", args.Select((a, i) => $"@{i} = {a}"));

        private static string PrimaryKeys(Change change) =>
            string.Join(" and ", change.Keys.Keys.Select((c, i) => $"{c} = @{i}"));

        private static List<string> ColumnTypes(TableInfo table, IEnumerable<string> columnNames) =>
            [.. columnNames.Select(c => table.ColumnTypes[c])];

        private long GetCurrentVersion(DatabaseInfo dbInfo)
        {
            try
            {
                using var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2005);
                var syncInfoTableExists = db.ExecuteScalar<string>("select top(1) name from sys.tables where name ='SyncInfo'") != null;
                long currentVersion;

                if (!syncInfoTableExists)
                {
                    Log.Info($"SyncInfo table does not exist in database {dbInfo.Name}");
                    currentVersion = db.ExecuteScalar<long?>("select CHANGE_TRACKING_CURRENT_VERSION()") ?? -1;
                    if (currentVersion < 0)
                    {
                        Log.Info($"Change tracking not enabled in database {dbInfo.Name}, assuming version 0");
                        currentVersion = 0;
                    }
                    else
                        Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");
                }
                else
                {
                    currentVersion = db.ExecuteScalar<long>("select top(1) Version from SyncInfo");
                    Log.Info($"Database {dbInfo.Name} is at version {currentVersion}");
                }

                return currentVersion;
            }
            catch (Exception ex) when (!Token.IsCancellationRequested)
            {
                Log.Error(ex, $"Error getting current version of destination database {dbInfo.Name}. Skipping this destination.");
                Error = true;
                return -1;
            }
        }
    }
}
