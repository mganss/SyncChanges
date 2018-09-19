using System.Collections.Generic;
using System.Linq;

namespace SyncChanges
{
    class Change
    {
        public TableInfo Table { get; set; }
        public long Version { get; set; }
        public long CreationVersion { get; set; }
        public char Operation { get; set; }
        public Dictionary<string, object> Keys { get; private set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Others { get; private set; } = new Dictionary<string, object>();
        public Dictionary<ForeignKeyConstraint, long> ForeignKeyConstraintsToDisable { get; private set; } = new Dictionary<ForeignKeyConstraint, long>();

        public object[] Values => Keys.Values.Concat(Others.Values).ToArray();

        public List<string> ColumnNames => Keys.Keys.Concat(Others.Keys).ToList();

        public object GetValue(string columnName)
        {
            if (!Keys.TryGetValue(columnName, out object o))
                if (!Others.TryGetValue(columnName, out o))
                    return null;
            return o;
        }
    }
}