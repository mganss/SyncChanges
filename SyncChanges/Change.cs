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
        public Dictionary<string, object> Keys { get; private set; } = [];
        public Dictionary<string, object> Others { get; private set; } = [];
        public Dictionary<ForeignKeyConstraint, long> ForeignKeyConstraintsToDisable { get; private set; } = [];

        public object[] GetValues() => [.. Keys.Values, .. Others.Values];

        public List<string> GetColumnNames() => [.. Keys.Keys, .. Others.Keys];

        public object GetValue(string columnName)
        {
            if (!Keys.TryGetValue(columnName, out object o) && !Others.TryGetValue(columnName, out o))
                return null;
            return o;
        }
    }
}