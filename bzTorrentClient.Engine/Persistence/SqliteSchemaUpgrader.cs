using System.Data;
using Microsoft.EntityFrameworkCore;

namespace bzTorrentClient.Engine.Persistence;

/// <summary>
/// <c>EnsureCreated()</c> only creates the schema for a brand-new database - it never
/// adds columns to an existing one when the entity model gains a property, so an existing
/// sessions.db from before a column was added throws "no such column" the moment EF tries
/// to read/write it. This is a lightweight stand-in for a full migrations pipeline: for
/// every table that already exists, it adds whatever columns the current model expects but
/// the database doesn't have yet (existing rows get the column's default value). Sufficient
/// for this app's simple, additive-only schema changes; a real migrations setup would be
/// warranted if columns ever need to be renamed, retyped, or removed.
/// </summary>
public static class SqliteSchemaUpgrader
{
    public static void EnsureColumnsExist(BzTorrentClientDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
            connection.Open();

        try
        {
            foreach (var entityType in db.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (tableName is null)
                    continue;

                var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = $"PRAGMA table_info(\"{tableName}\");";
                    using var reader = pragma.ExecuteReader();
                    while (reader.Read())
                        existingColumns.Add(reader.GetString(reader.GetOrdinal("name")));
                }

                // An empty result means the table itself doesn't exist yet - EnsureCreated()'s
                // job, not ours; it will have created it with every current column already.
                if (existingColumns.Count == 0)
                    continue;

                foreach (var property in entityType.GetProperties())
                {
                    var columnName = property.GetColumnName();
                    if (existingColumns.Contains(columnName))
                        continue;

                    using var alter = connection.CreateCommand();
                    alter.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {SqliteColumnDefinition(property.ClrType, property.IsNullable)};";
                    alter.ExecuteNonQuery();
                }
            }
        }
        finally
        {
            if (wasClosed)
                connection.Close();
        }
    }

    /// <summary>
    /// Existing rows get NULL for a newly-added column unless a default is specified - for
    /// a non-nullable property that throws the moment EF reads it back ("data is NULL"),
    /// so every non-nullable column needs an explicit NOT NULL DEFAULT matching its type.
    /// </summary>
    private static string SqliteColumnDefinition(Type clrType, bool isNullable)
    {
        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        var sqlType = underlying switch
        {
            _ when underlying == typeof(byte[]) => "BLOB",
            _ when underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal) => "REAL",
            _ when underlying == typeof(string) || underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset)
                || underlying == typeof(Guid) || underlying.IsEnum => "TEXT",
            _ => "INTEGER", // long, int, bool, ...
        };

        if (isNullable)
            return $"{sqlType} NULL";

        var defaultLiteral = underlying switch
        {
            _ when underlying == typeof(string) => "''",
            _ when underlying == typeof(byte[]) => "x''",
            _ when underlying == typeof(DateTime) => "'0001-01-01T00:00:00.0000000'",
            _ when underlying == typeof(DateTimeOffset) => "'0001-01-01T00:00:00.0000000+00:00'",
            _ => "0",
        };

        return $"{sqlType} NOT NULL DEFAULT {defaultLiteral}";
    }
}
