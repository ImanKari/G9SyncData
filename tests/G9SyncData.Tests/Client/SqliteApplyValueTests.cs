using System.Globalization;
using G9SyncData.Client.Model;
using G9SyncData.Client.Sqlite;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using AppConnection = Microsoft.Data.Sqlite.SqliteConnection;

namespace G9SyncData.Tests.Client;

/// <summary>
///     The allocation-free apply path binds values straight from frame spans (ADR-0034). What it stores must be exactly what the boxed path
///     stored - the forms the app's ORM reads - for every column type, both storage options, and every key type; and an empty string or
///     an empty blob must stay empty, not become NULL (SQLite reads the null pointer an empty span pins as NULL).
/// </summary>
public sealed class SqliteApplyValueTests : IAsyncDisposable
{
    private static readonly Guid GuidKey = Guid.Parse("8F3EB2A1-0C4D-4E5F-9A6B-7C8D9E0F1A2B");
    private static readonly Guid GuidValue = Guid.Parse("0A1B2C3D-4E5F-4061-8273-9485A6B7C8D9");
    private static readonly DateTime Moment = new DateTime(2026, 9, 17, 10, 11, 12, DateTimeKind.Unspecified).AddTicks(1234567);
    private static readonly DateTimeOffset MomentOffset = new(2026, 9, 17, 10, 11, 12, TimeSpan.FromMinutes(210));

    private static readonly (string Name, G9ESyncColumnType Type)[] Columns =
    [
        ("Flag", G9ESyncColumnType.Boolean), ("Small", G9ESyncColumnType.Int32), ("Big", G9ESyncColumnType.Int64), ("Ratio", G9ESyncColumnType.Double),
        ("Amount", G9ESyncColumnType.Decimal), ("Label", G9ESyncColumnType.String), ("Empty", G9ESyncColumnType.String), ("Ref", G9ESyncColumnType.Guid),
        ("At", G9ESyncColumnType.DateTime), ("AtOffset", G9ESyncColumnType.DateTimeOffset), ("Span", G9ESyncColumnType.TimeSpan),
        ("Data", G9ESyncColumnType.Bytes), ("NoData", G9ESyncColumnType.Bytes), ("Missing", G9ESyncColumnType.String)
    ];

    private readonly string _path = Path.Combine(Path.GetTempPath(), "g9sync-tests", Guid.NewGuid().ToString("N") + ".db");
    private G9CSqliteSyncStore? _store;

    [Theory]
    [InlineData(G9ESyncColumnType.Guid, false, false)]
    [InlineData(G9ESyncColumnType.Int64, true, true)]
    [InlineData(G9ESyncColumnType.String, false, true)]
    public async Task Every_column_type_is_stored_in_the_form_the_app_reads(G9ESyncColumnType keyType, bool decimalAsText, bool ticks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var model = new G9CSyncClientModelBuilder().Table("farm.Mixed", "Mixed", t =>
        {
            t.Key("Id", keyType);
            foreach (var (name, type) in Columns) t.Column(name, type);
        }).Build();
        _store = new G9CSqliteSyncStore(new G9CSqliteSyncStoreOptions
        {
            DatabasePath = _path,
            DecimalStorage = decimalAsText ? G9ESqliteDecimalStorage.Text : G9ESqliteDecimalStorage.Real,
            DateTimeStorage = ticks ? G9ESqliteDateTimeStorage.Ticks : G9ESqliteDateTimeStorage.Text
        });
        await _store.InitializeAsync(model, CancellationToken.None);
        var table = new G9DtNegotiatedTable
        {
            TableId = 1, Name = "farm.Mixed", KeyColumn = "Id", KeyType = keyType, Direction = G9ESyncDirection.Bidirectional,
            Columns = Columns.Select(c => new G9DtColumnModel { Name = c.Name, Type = c.Type, Nullable = true }).ToList()
        };
        await _store.SaveHelloAsync(new G9DtHelloResult { Epoch = Guid.NewGuid(), ServerTimeUtc = DateTime.UtcNow, Tables = [table] }, CancellationToken.None);
        var subscription = await _store.EnsureSubscriptionAsync("mixed", new G9CSyncParameters(), false, null, false, CancellationToken.None);

        using var writer = new G9CRowBatchWriter();
        writer.BeginSection(G9ERowBatchOp.Upsert, 1, keyType, Columns.Select(c => c.Type).ToArray());
        switch (keyType)
        {
            case G9ESyncColumnType.Guid: writer.BeginRow(GuidKey, 4); break;
            case G9ESyncColumnType.Int64: writer.BeginRow(9_007_199_254_740_993L, 4); break;
            default: writer.BeginRow("Key With Spaces ۱", 4); break;
        }

        writer.WriteBoolean(true);
        writer.WriteInt32(-42);
        writer.WriteInt64(long.MaxValue);
        writer.WriteDouble(0.1);
        writer.WriteDecimal(123.4500m);
        writer.WriteString("سلام, world");
        writer.WriteString(string.Empty);
        writer.WriteGuid(GuidValue);
        writer.WriteDateTime(Moment);
        writer.WriteDateTimeOffset(MomentOffset);
        writer.WriteTimeSpan(TimeSpan.FromTicks(987654321));
        writer.WriteBytes([1, 2, 3]);
        writer.WriteBytes([]);
        writer.WriteNull();
        writer.EndRow();
        writer.EndSection();

        var report = await _store.ApplyReconcileAsync(subscription.Key, table, [writer.ToFrame()], [], [], G9EMoveOutPolicy.Delete, CancellationToken.None);
        Assert.Equal(1, report.Upserts);

        var expectedKey = keyType switch
        {
            G9ESyncColumnType.Guid => "8f3eb2a1-0c4d-4e5f-9a6b-7c8d9e0f1a2b",
            G9ESyncColumnType.Int64 => "9007199254740993",
            _ => "Key With Spaces ۱"
        };
        Assert.Equal(expectedKey, await ScalarAsync("SELECT pk FROM g9_rows WHERE tid = (SELECT tid FROM g9_tables WHERE local_name = 'Mixed')"));
        Assert.Equal(keyType == G9ESyncColumnType.Int64 ? "integer" : "text", await ScalarAsync("SELECT typeof(Id) FROM Mixed"));
        Assert.Equal(expectedKey, await ScalarAsync("SELECT CAST(Id AS TEXT) FROM Mixed"));

        var row = await RowAsync("SELECT Flag, Small, Big, Ratio, Amount, typeof(Amount), Label, Empty, typeof(Empty), Ref, At, typeof(At), AtOffset, Span, " +
                                 "hex(Data), typeof(NoData), length(NoData), typeof(Missing) FROM Mixed");
        Assert.Equal(1L, row[0]);
        Assert.Equal(-42L, row[1]);
        Assert.Equal(long.MaxValue, row[2]);
        Assert.Equal(0.1, row[3]);
        if (decimalAsText)
        {
            Assert.Equal("123.4500", row[4]);
            Assert.Equal("text", row[5]);
        }
        else
        {
            Assert.Equal(123.45, row[4]);
            Assert.Equal("real", row[5]);
        }

        Assert.Equal("سلام, world", row[6]);
        Assert.Equal(string.Empty, row[7]);
        Assert.Equal("text", row[8]);
        Assert.Equal("0a1b2c3d-4e5f-4061-8273-9485a6b7c8d9", row[9]);
        if (ticks)
        {
            Assert.Equal(Moment.Ticks, row[10]);
            Assert.Equal("integer", row[11]);
        }
        else
        {
            Assert.Equal(Moment.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture), row[10]);
            Assert.Equal("text", row[11]);
        }

        Assert.Equal("2026-09-17T10:11:12.0000000+03:30", row[12]);
        Assert.Equal(987654321L, row[13]);
        Assert.Equal("010203", row[14]);
        Assert.Equal("blob", row[15]);
        Assert.Equal(0L, row[16]);
        Assert.Equal("null", row[17]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null) await _store.DisposeAsync();
        using (var helper = new AppConnection("Data Source=" + _path)) AppConnection.ClearPool(helper);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private async Task<object?> ScalarAsync(string sql) => (await RowAsync(sql))[0];

    private async Task<object?[]> RowAsync(string sql)
    {
        await using var connection = new AppConnection("Data Source=" + _path);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var values = new object?[reader.FieldCount];
        for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }
}
