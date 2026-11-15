using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SyncChanges
{
    /// <summary>
    /// Provides data for the <see cref="Synchronizer.Synced"/> event.
    /// </summary>
    public class SyncEventArgs: EventArgs
    {
        /// <summary>
        /// Gets or sets the affected replication set.
        /// </summary>
        public ReplicationSet ReplicationSet { get; set; }

        /// <summary>
        /// Gets or sets the new version.
        /// </summary>
        public long Version { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether all destinations of the replication set are now at <see cref="Version"/>.
        /// </summary>
        public bool Success { get; set; }
    }
}
