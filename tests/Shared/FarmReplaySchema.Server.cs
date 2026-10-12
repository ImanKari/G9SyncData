using System.Globalization;
using G9SyncData.Server.Model;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace G9SyncData.DeviceTests.Shared;

public static partial class FarmReplaySchema
{
    /// <summary>The PostgreSQL schema the replayed tables live in (the harness's own test schema is <c>farm</c>).</summary>
    public const string Schema = "shah";

    /// <summary>The server tables: the client model's columns with PostgreSQL types, every key a uuid primary key.</summary>
    public static string Sql => $"CREATE SCHEMA {Schema};\n" + string.Concat(Client.Tables.Select(table =>
        $"CREATE TABLE {Schema}.\"{table.LocalName}\" (\"{table.KeyColumn}\" uuid NOT NULL PRIMARY KEY" +
        string.Concat(table.Columns.Select(c => $", \"{c.Name}\" {PgType(c.Type)}{(c.Nullable ? string.Empty : " NOT NULL")}")) + ");\n"));

    /// <summary>The server model: the same tables, one unfiltered subscription (the server database holds one farm only).</summary>
    public static void ConfigureServer(G9CSyncModelBuilder model)
    {
        foreach (var table in Client.Tables)
        {
            model.Table(Schema, table.LocalName, t =>
            {
                t.Key(table.KeyColumn);
                foreach (var column in table.Columns) t.Column(column.Name, column.Type, column.Nullable);
            });
        }

        model.Subscription(Subscription, s =>
        {
            foreach (var name in Tables) s.Include($"{Schema}.{name}");
        });
    }

    /// <summary>
    ///     Copies one farm's farm-site rows out of an AgriPad device database (<paramref name="deviceDatabase" />, opened read-only) into
    ///     the server tables with binary COPY. The farm is selected as AgriPad's farm-site scope selects it: ridges of the farm's
    ///     blocks of its fields, their pots, the current placements of those pots, their trees, those trees' grafts. Returns the rows
    ///     per table.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, long>> LoadAsync(NpgsqlDataSource dataSource, string deviceDatabase, Guid farmId)
    {
        var source = new SqliteConnectionStringBuilder { DataSource = deviceDatabase, Mode = SqliteOpenMode.ReadOnly }.ToString();
        await using var sqlite = new SqliteConnection(source);
        await sqlite.OpenAsync();
        await using (var temp = sqlite.CreateCommand())
        {
            // The farm's keys, once, in TEMP tables (a read-only connection may still write its temp schema).
            temp.CommandText = """
                CREATE TEMP TABLE r AS SELECT r.Id FROM Ridge r JOIN Block b ON b.Id = r.BlockId JOIN Field f ON f.Id = b.FieldId
                    WHERE f.FarmId = $farm COLLATE NOCASE;
                CREATE TEMP TABLE p AS SELECT Id FROM Pot WHERE RidgeId IN (SELECT Id FROM temp.r);
                CREATE TEMP TABLE pl AS SELECT Id, TreeId FROM Placement WHERE IsCurrent = 1 AND PotId IN (SELECT Id FROM temp.p);
                CREATE TEMP TABLE t AS SELECT DISTINCT TreeId AS Id FROM temp.pl;
                """;
            temp.Parameters.AddWithValue("$farm", farmId.ToString("D"));
            await temp.ExecuteNonQueryAsync();
        }

        var filters = new Dictionary<string, string>
        {
            ["Ridge"] = "Id IN (SELECT Id FROM temp.r)",
            ["Pot"] = "Id IN (SELECT Id FROM temp.p)",
            ["Placement"] = "Id IN (SELECT Id FROM temp.pl)",
            ["Tree"] = "Id IN (SELECT Id FROM temp.t)",
            ["Graft"] = "TreeId IN (SELECT Id FROM temp.t)"
        };

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var pg = await dataSource.OpenConnectionAsync();
        foreach (var table in Client.Tables)
        {
            var columns = new List<(string Name, G9ESyncColumnType Type)> { (table.KeyColumn, table.KeyType) };
            columns.AddRange(table.Columns.Select(c => (c.Name, c.Type)));
            await using var read = sqlite.CreateCommand();
            read.CommandText = $"SELECT {string.Join(", ", columns.Select(c => $"[{c.Name}]"))} FROM [{table.LocalName}] WHERE {filters[table.LocalName]}";
            await using var reader = await read.ExecuteReaderAsync();
            await using var copy = await pg.BeginBinaryImportAsync(
                $"COPY {Schema}.\"{table.LocalName}\" ({string.Join(", ", columns.Select(c => $"\"{c.Name}\""))}) FROM STDIN (FORMAT BINARY)");
            long rows = 0;
            while (await reader.ReadAsync())
            {
                await copy.StartRowAsync();
                for (var i = 0; i < columns.Count; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        await copy.WriteNullAsync();
                        continue;
                    }

                    switch (columns[i].Type)
                    {
                        case G9ESyncColumnType.Guid: await copy.WriteAsync(Guid.Parse(reader.GetString(i)), NpgsqlDbType.Uuid); break;
                        case G9ESyncColumnType.Int32: await copy.WriteAsync(checked((int)reader.GetInt64(i)), NpgsqlDbType.Integer); break;
                        case G9ESyncColumnType.Boolean: await copy.WriteAsync(reader.GetInt64(i) != 0, NpgsqlDbType.Boolean); break;
                        case G9ESyncColumnType.DateTime:
                            await copy.WriteAsync(DateTime.Parse(reader.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None), NpgsqlDbType.Timestamp);
                            break;
                        default: await copy.WriteAsync(reader.GetString(i), NpgsqlDbType.Text); break;
                    }
                }

                rows++;
            }

            await copy.CompleteAsync();
            counts[table.LocalName] = rows;
        }

        return counts;
    }

    private static string PgType(G9ESyncColumnType type) => type switch
    {
        G9ESyncColumnType.Guid => "uuid",
        G9ESyncColumnType.Int32 => "integer",
        G9ESyncColumnType.Boolean => "boolean",
        G9ESyncColumnType.DateTime => "timestamp without time zone",
        _ => "text"
    };
}
