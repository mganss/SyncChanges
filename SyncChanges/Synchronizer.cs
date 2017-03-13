using Humanizer;
using NLog;
using NPoco;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SyncChanges
{
    public class Synchronizer
    {
        public bool DryRun { get; set; } = false;
        public int Timeout { get; set; } = 0;

        static Logger Log = LogManager.GetCurrentClassLogger();
        Config Config { get; set; }
        bool Error { get; set; }

        public Synchronizer(Config config)
        {
            if (config == null) throw new ArgumentException("config is null", nameof(config));
            Config = config;
        }

        public bool Sync()
        {
            Error = false;

            foreach (var replicationSet in Config.ReplicationSets)
            {
                Log.Info($"Starting replication for replication set {replicationSet.Name}");
                if (Timeout != 0)
                    Log.Info($"Command timeout is {"second".ToQuantity(Timeout)}");

                var tables = GetTables(replicationSet.Source);
                if (replicationSet.Tables != null && replicationSet.Tables.Any())
                    tables = tables.Where(t => replicationSet.Tables.Contains(t.Name.Trim('[', ']'))).ToList();

                if (!tables.Any())
                {
                    Log.Warn("No tables to replicate (check if change tracking is enabled)");
                    continue;
                }

                Log.Info($"Replicating {"table".ToQuantity(tables.Count, ShowQuantityAs.None)} {string.Join(", ", tables.Select(t => t.Name))}");

                var destinationsByVersion = replicationSet.Destinations.GroupBy(d => GetCurrentVersion(d))
                    .Where(d => d.Key >= 0).ToList();

                foreach (var destinations in destinationsByVersion)
                    Replicate(replicationSet.Source, destinations, tables);
            }

            Log.Info($"Finished replication {(Error ? "with" : "without")} errors");

            return !Error;
        }

        private IList<TableInfo> GetTables(DatabaseInfo dbInfo)
        {
            try
            {
                using (var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2008))
                {
                    var sql = @"select TableName, ColumnName, iif(max(cast(is_primary_key as tinyint)) = 1, 1, 0) PrimaryKey from
                        (
                        select ('[' + s.name + '].[' + t.name + ']') TableName, ('[' + COL_NAME(t.object_id, a.column_id) + ']') ColumnName,
                        i.is_primary_key
                        from sys.change_tracking_tables tr
                        join sys.tables t on t.object_id = tr.object_id
                        join sys.schemas s on s.schema_id = t.schema_id
                        join sys.columns a on a.object_id = t.object_id
                        left join sys.index_columns c on c.object_id = t.object_id and c.column_id = a.column_id
                        left join sys.indexes i on i.object_id = t.object_id and i.index_id = c.index_id
                        where a.is_computed = 0
                        ) X
                        group by TableName, ColumnName
                        order by TableName, ColumnName";

                    var tables = db.Fetch<dynamic>(sql).GroupBy(t => t.TableName)
                        .Select(g => new TableInfo
                        {
                            Name = (string)g.Key,
                            KeyColumns = g.Where(c => (int)c.PrimaryKey > 0).Select(c => (string)c.ColumnName).ToList(),
                            OtherColumns = g.Where(c => (int)c.PrimaryKey == 0).Select(c => (string)c.ColumnName).ToList()
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
                        table.ForeignKeyConstraints = fks.Where(f => f.TableName == table.Name).ToList();

                    return tables;
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Error getting tables to replicate from source database");
                throw;
            }
        }

        private Database GetDatabase(string connectionString, DatabaseType databaseType = null)
        {
            var db = new Database(connectionString, databaseType ?? DatabaseType.SqlServer2005);

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
                try
                {
                    Log.Info($"Replicating {"change".ToQuantity(changeInfo.Changes.Count)} to destination {destination.Name}");

                    using (var db = GetDatabase(destination.ConnectionString, DatabaseType.SqlServer2005))
                    using (var transaction = db.GetTransaction(System.Data.IsolationLevel.ReadUncommitted))
                    {
                        try
                        {
                            var changes = changeInfo.Changes;
                            var pendingChanges = new List<Change>();
                            var disabledForeignKeyConstraints = new Dictionary<ForeignKeyConstraint, long>();

                            for (int i = 0; i < changes.Count; i++)
                            {
                                var change = changes[i];
                                Log.Debug($"Replicating change #{i + 1} of {changes.Count} (Version {change.Version}, CreationVersion {change.CreationVersion})");

                                foreach (var fk in change.ForeignKeyConstraintsToDisable)
                                {
                                    long untilVersion;
                                    if (disabledForeignKeyConstraints.TryGetValue(fk.Key, out untilVersion))
                                    {
                                        // FK is already disabled, check if it needs to be deferred further than currently planned
                                        if (fk.Value > untilVersion)
                                            disabledForeignKeyConstraints[fk.Key] = fk.Value;
                                    }
                                    else
                                    {
                                        DisableForeignKeyConstraint(db, fk.Key);
                                        disabledForeignKeyConstraints[fk.Key] = fk.Value;
                                    }
                                }

                                PerformChange(db, change);

                                if ((i + 1) >= changes.Count || changes[i + 1].CreationVersion > change.CreationVersion) // there may be more than one change with the same CreationVersion
                                {
                                    foreach (var fk in disabledForeignKeyConstraints.Where(f => f.Value <= change.CreationVersion).Select(f => f.Key).ToList())
                                    {
                                        ReenableForeignKeyConstraint(db, fk);
                                        disabledForeignKeyConstraints.Remove(fk);
                                    }
                                }
                            }

                            if (!DryRun)
                            {
                                SetSyncVersion(db, changeInfo.Version);
                                transaction.Complete();
                            }

                            Log.Info($"Destination {destination.Name} now at version {changeInfo.Version}");
                        }
                        catch (Exception ex)
                        {
                            Error = true;
                            Log.Error(ex, $"Error replicating changes to destination {destination.Name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Error = true;
                    Log.Error(ex, $"Error replicating changes to destination {destination.Name}");
                }
            }
        }

        private void ReenableForeignKeyConstraint(Database db, ForeignKeyConstraint fk)
        {
            Log.Debug($"Re-enabling foreign key constraint {fk.ForeignKeyName}");
            var sql = $"alter table {fk.TableName} with check check constraint {fk.ForeignKeyName}";
            if (!DryRun)
                db.Execute(sql);
        }

        private void DisableForeignKeyConstraint(Database db, ForeignKeyConstraint fk)
        {
            Log.Debug($"Disabling foreign key constraint {fk.ForeignKeyName}");
            var sql = $"alter table {fk.TableName} nocheck constraint {fk.ForeignKeyName}";
            if (!DryRun)
                db.Execute(sql);
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
                    sql += " order by coalesce(c.SYS_CHANGE_CREATION_VERSION, c.SYS_CHANGE_VERSION)";

                    Log.Debug($"Retrieving changes for table {tableName}: {sql}");

                    db.OpenSharedConnection();
                    var cmd = db.CreateCommand(db.Connection, System.Data.CommandType.Text, sql, destinationVersion);

                    using (var reader = cmd.ExecuteReader())
                    {
                        var numChanges = 0;

                        while (reader.Read())
                        {
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
                                change.Keys[table.KeyColumns[i]] = reader.GetValue(col);
                            for (int i = 0; i < table.OtherColumns.Count; i++, col++)
                                change.Others[table.OtherColumns[i]] = reader.GetValue(col);

                            changes.Add(change);
                            numChanges++;
                        }

                        Log.Info($"Table {tableName} has {"change".ToQuantity(numChanges)}");
                    }
                }

                if (snapshotIsolationEnabled)
                    db.CompleteTransaction();
            }

            changeInfo.Changes.AddRange(changes.OrderBy(c => c.CreationVersion).ThenBy(c => c.Table.Name));

            ComputeForeignKeyConstraintsToDisable(changeInfo);

            return changeInfo;
        }

        private void ComputeForeignKeyConstraintsToDisable(ChangeInfo changeInfo)
        {
            var changes = changeInfo.Changes;

            for (int i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.CreationVersion < change.Version) // was inserted then later updated
                {
                    for (int j = i + 1; j < changes.Count; j++)
                    {
                        var intermediateChange = changes[j];
                        if (intermediateChange.CreationVersion > change.Version) // created later than last update to change
                            break;
                        if (intermediateChange.Operation != 'I') continue;

                        // let's look at intermediateChange if it collides with change
                        foreach (var fk in change.Table.ForeignKeyConstraints.Where(f => f.ReferencedTableName == intermediateChange.Table.Name))
                        {
                            var val = change.GetValue(fk.ColumnName);
                            var refVal = intermediateChange.GetValue(fk.ReferencedColumnName);
                            if (val != null && val.Equals(refVal))
                            {
                                // this foreign key constraint needs to be disabled
                                Log.Info($"Foreign key constraint {fk.ForeignKeyName} needs to be disabled for change #{i + 1} from version {change.CreationVersion} until version {intermediateChange.CreationVersion}");
                                change.ForeignKeyConstraintsToDisable[fk] = intermediateChange.CreationVersion;
                            }
                        }
                    }
                }
            }
        }

        private void PerformChange(Database db, Change change)
        {
            var table = change.Table;
            var tableName = table.Name;
            var operation = change.Operation;

            switch (operation)
            {
                // Insert
                case 'I':
                    var insertColumnNames = change.ColumnNames;
                    var insertSql = $"set IDENTITY_INSERT {tableName} ON; " +
                        string.Format("insert into {0} ({1}) values ({2}); ", tableName,
                        string.Join(", ", insertColumnNames),
                        string.Join(", ", Parameters(insertColumnNames.Count))) +
                        $"set IDENTITY_INSERT {tableName} OFF";
                    var insertValues = change.Values;
                    Log.Debug($"Executing insert: {insertSql} ({FormatArgs(insertValues)})");
                    if (!DryRun)
                        db.Execute(insertSql, insertValues);
                    break;

                // Update
                case 'U':
                    var updateColumnNames = change.Others.Keys.ToList();
                    var updateSql = string.Format("update {0} set {1} where {2}", tableName,
                        string.Join(", ", updateColumnNames.Select((c, i) => $"{c} = @{i + change.Keys.Count}")),
                        PrimaryKeys(table, change));
                    var updateValues = change.Values;
                    Log.Debug($"Executing update: {updateSql} ({FormatArgs(updateValues)})");
                    if (!DryRun)
                        db.Execute(updateSql, updateValues);
                    break;

                // Delete
                case 'D':
                    var deleteSql = string.Format("delete from {0} where {1}", tableName, PrimaryKeys(table, change));
                    var deleteValues = change.Keys.Values.ToArray();
                    Log.Debug($"Executing delete: {deleteSql} ({FormatArgs(deleteValues)})");
                    if (!DryRun)
                        db.Execute(deleteSql, deleteValues);
                    break;
            }
        }

        private static string FormatArgs(object[] args) => string.Join(", ", args.Select((a, i) => $"@{i} = {a}"));

        private static string PrimaryKeys(TableInfo table, Change change) =>
            string.Join(" and ", change.Keys.Keys.Select((c, i) => $"{c} = @{i}"));

        private static IEnumerable<string> Parameters(int n) => Enumerable.Range(0, n).Select(c => "@" + c);

        private long GetCurrentVersion(DatabaseInfo dbInfo)
        {
            try
            {
                using (var db = GetDatabase(dbInfo.ConnectionString, DatabaseType.SqlServer2005))
                {
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
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error getting current version of destination database {dbInfo.Name}. Skipping this destination.");
                Error = true;
                return -1;
            }
        }
    }
}
