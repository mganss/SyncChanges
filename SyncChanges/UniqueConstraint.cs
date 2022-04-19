using System;
using System.Collections.Generic;

namespace SyncChanges
{
    class UniqueColumn
    {
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public string IndexName { get; set; }
        public bool IsConstraint { get; set; }
    }

    class UniqueConstraint
    {
        public string TableName { get; set; }
        public List<string> ColumnNames { get; set; } = new();
        public string IndexName { get; set; }
        public string FullName => TableName + ":" + string.Join("_", ColumnNames);
        public bool IsConstraint { get; set; }

        public override bool Equals(Object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var uq = (UniqueConstraint)obj;
            return FullName == uq.FullName;
        }

        public override int GetHashCode()
        {
            return FullName.GetHashCode();
        }
    }
}