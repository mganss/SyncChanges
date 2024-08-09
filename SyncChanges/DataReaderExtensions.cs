using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Text;

namespace SyncChanges
{
    internal static class DataReaderExtensions
    {

        public static object[] GetNullableTypeMap(this DbDataReader reader)
        {

            var nullTypeMap = new object[reader.FieldCount];

            for (int i = 0; i < nullTypeMap.Length; i++)
            {
                if (reader.IsDBNull(i))
                {
                    switch (reader.GetDataTypeName(i).ToUpper())
                    {
                        case "VARBINARY":
                            nullTypeMap[i] = SqlBinary.Null;
                            break;
                    }
                }
            }

            return nullTypeMap;
        }


    }
}