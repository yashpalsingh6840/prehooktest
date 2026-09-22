using System.Data.Common;

namespace SyntheticScriptComponentSeams.Sql;

public static class SyntheticScriptSeamsTargetSqlRowReader
{
    public static SyntheticScriptSeamsTargetSqlRow Read(DbDataReader reader) => new()
    {
        ID = reader.GetFieldValue<int>(reader.GetOrdinal("ID")),
        FirstName = reader.GetFieldValue<string>(reader.GetOrdinal("FirstName")),
        LastName = reader.GetFieldValue<string>(reader.GetOrdinal("LastName")),
    };
}
