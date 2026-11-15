using System;
using System.Collections.Generic;
using System.Linq;

namespace SyncChanges
{
    /// <summary>
    /// Compares column values roughly as SQL Server compares them for a foreign key: strings without regard to case and
    /// trailing spaces, as in the usual case-insensitive collations, and binary values by their bytes.
    /// </summary>
    /// <remarks>
    /// Values that SQL Server takes as different may compare as equal, e.g. under a case-sensitive collation, but not
    /// the other way round. Treating two values as equal only makes the ordering of changes more cautious than needed.
    /// </remarks>
    internal sealed class ValueComparer : IEqualityComparer<object>
    {
        public static readonly ValueComparer Instance = new();

        private ValueComparer() { }

        public new bool Equals(object x, object y)
        {
            if (x is string s && y is string t)
                return string.Equals(s.TrimEnd(' '), t.TrimEnd(' '), StringComparison.OrdinalIgnoreCase);
            if (x is byte[] a && y is byte[] b)
                return a.SequenceEqual(b);
            return object.Equals(x, y);
        }

        public int GetHashCode(object obj)
        {
            switch (obj)
            {
                case null: return 0;
                case string s: return StringComparer.OrdinalIgnoreCase.GetHashCode(s.TrimEnd(' '));
                case byte[] b:
                    var hash = b.Length;
                    foreach (var x in b.Take(16)) hash = hash * 31 + x;
                    return hash;
                default: return obj.GetHashCode();
            }
        }
    }
}
