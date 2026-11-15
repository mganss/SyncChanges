using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SyncChanges
{
    /// <summary>
    /// Represents configuration information for the replication of database changes.
    /// </summary>
    public class Config
    {
        /// <summary>
        /// Gets the replication sets.
        /// </summary>
        /// <value>
        /// The replication sets.
        /// </value>
        public List<ReplicationSet> ReplicationSets { get; private set; } = [];

        /// <summary>
        /// Gets or sets the database command timeout in seconds; 0 leaves the default of the database client (30 seconds).
        /// If not set, the application's setting applies.
        /// </summary>
        /// <value>
        /// The database command timeout in seconds.
        /// </value>
        public int? Timeout { get; set; }

        /// <summary>
        /// Gets or sets the minimum time in seconds between replications in a synchronization loop.
        /// If not set, the application's setting applies.
        /// </summary>
        /// <value>
        /// The replication interval in seconds.
        /// </value>
        public int? Interval { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of statements sent to a destination database in one round trip.
        /// If not set, the application's setting applies.
        /// </summary>
        /// <value>
        /// The maximum number of statements per round trip.
        /// </value>
        public int? BatchSize { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to only perform a test run, without altering destination databases.
        /// If not set, the application's setting applies.
        /// </summary>
        /// <value>
        /// <c>true</c> for a test run; otherwise, <c>false</c>.
        /// </value>
        public bool? DryRun { get; set; }
    }

    /// <summary>
    /// Represents a replication sets, i.e. the combination of a source database and one or more destination databases.
    /// </summary>
    public class ReplicationSet
    {
        /// <summary>
        /// Gets or sets the name of the replication set. This is just for identification in logs etc.
        /// </summary>
        /// <value>
        /// The name of the replication set.
        /// </value>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the source database.
        /// </summary>
        /// <value>
        /// The source database.
        /// </value>
        public DatabaseInfo Source { get; set; }

        /// <summary>
        /// Gets the destination databases.
        /// </summary>
        /// <value>
        /// The destination databases.
        /// </value>
        public List<DatabaseInfo> Destinations { get; private set; } = [];

        /// <summary>
        /// Gets or sets the names of the tables to be replicated. If this is empty, all (non-system) tables will be replicated.
        /// </summary>
        /// <value>
        /// The tables to be replicated.
        /// </value>
        public List<string> Tables { get; set; } = [];
    }

    /// <summary>
    /// Represents information about a database.
    /// </summary>
    public class DatabaseInfo
    {
        /// <summary>
        /// Gets or sets the name of the database. Used solely for identification in logs etc.
        /// </summary>
        /// <value>
        /// The name of the database.
        /// </value>
        public string Name { get; set; }

        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        /// <value>
        /// The connection string.
        /// </value>
        public string ConnectionString { get; set; }
    }
}
