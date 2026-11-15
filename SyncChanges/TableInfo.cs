using System.Collections.Generic;

namespace SyncChanges
{
    class TableInfo
    {
        public string Name { get; set; }
        public IList<string> KeyColumns { get; set; }
        public IList<string> OtherColumns { get; set; }
        public IList<ForeignKeyConstraint> ForeignKeyConstraints { get; set; }
        public IList<UniqueConstraint> UniqueConstraints { get; set; }
        public bool HasIdentity { get; set; }

        /// <summary>
        /// Gets or sets the types of the columns, by column name, as they are declared as parameters (e.g. <c>nvarchar(200)</c>).
        /// </summary>
        public IDictionary<string, string> ColumnTypes { get; set; }
    }
}