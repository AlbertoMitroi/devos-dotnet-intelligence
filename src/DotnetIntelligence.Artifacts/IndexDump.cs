using System.Text;
using System.Text.Json.Nodes;
using DotnetIntelligence.Contracts;
using Microsoft.Data.Sqlite;

namespace DotnetIntelligence.Artifacts;

/// <summary>
/// Canonical dump of index.sqlite (5.2): every table (FTS5 shadow tables and SQLite internals excluded), ordered by
/// name; each row is a canonical JSON array of its column values, rows sorted ordinally; one "table\trow" line each.
/// This is what must be identical across runs and architectures, whatever the bytes of the SQLite file.
/// </summary>
public static class IndexDump
{
    public static string Dump(string indexPath)
    {
        using var connection = IndexWriter.Open(indexPath, readOnly: true);
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select name from sqlite_master where type = 'table' and name not like 'search_%' and name not like 'sqlite_%' order by name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        var output = new StringBuilder();
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            var rows = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = $"select * from \"{table}\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new JsonArray();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row.Add(reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                    {
                        long number => JsonValue.Create(number),
                        double number => JsonValue.Create(number),
                        string text => JsonValue.Create(text),
                        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
                        var other => JsonValue.Create(Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)),
                    });
                }
                rows.Add(CanonicalJson.Serialize(row));
            }
            foreach (var row in rows.Order(StringComparer.Ordinal))
            {
                output.Append(table).Append('\t').Append(row).Append('\n');
            }
        }
        return output.ToString();
    }

    /// <summary>Row count per table, for verify and the report.</summary>
    public static IReadOnlyDictionary<string, long> Counts(SqliteConnection connection, IEnumerable<string> tables)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"select count(*) from \"{table}\"";
            counts[table] = (long)command.ExecuteScalar()!;
        }
        return counts;
    }
}
