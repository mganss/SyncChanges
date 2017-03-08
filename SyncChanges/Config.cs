using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SyncChanges
{
    public class Config
    {
        public List<ReplicationSet> ReplicationSets { get; private set; } = new List<ReplicationSet>();
    }

    public class ReplicationSet
    {
        public string Name { get; set; }
        public DatabaseInfo Source { get; set; }
        public List<DatabaseInfo> Destinations { get; private set; } = new List<DatabaseInfo>();
        public List<string> Tables { get; set; }
    }

    public class DatabaseInfo
    {
        public string Name { get; set; }
        public string ConnectionString { get; set; }
    }
}
