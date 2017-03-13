using System;

namespace SyncChanges
{
    class ForeignKeyConstraint
    {
        public string TableName { get; set; }
        public string ColumnName { get; set; }
        public string ReferencedTableName { get; set; }
        public string ReferencedColumnName { get; set; }
        public string ForeignKeyName { get; set; }
        public string FullName => TableName + ":" + ForeignKeyName;

        public override bool Equals(Object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var fk = (ForeignKeyConstraint)obj;
            return FullName == fk.FullName;
        }

        public override int GetHashCode()
        {
            return FullName.GetHashCode();
        }
    }
}