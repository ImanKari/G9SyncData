using G9SyncData.Protocol;

namespace G9SyncData.Tests.Protocol;

/// <summary>
///     The DTO samples both halves of the TypeScript parity contract use: <see cref="JsGoldenVectorsTests" /> serializes the server's
///     answers to golden files, and checks that what the TypeScript client sends deserializes on the server to exactly these requests.
///     The TypeScript tests build the same requests (js/packages/syncdata/test/golden/samples.ts) - change both together.
/// </summary>
internal static class JsGoldenSamples
{
    public static readonly Guid ClientId = Guid.Parse("6f1c2a7e-3b4d-4e5f-8a9b-0c1d2e3f4a5b");
    public static readonly Guid Epoch = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    public static readonly Guid CommandId = Guid.Parse("a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d");

    public static G9DtSubscriptionRef Subscription() => new()
    {
        Name = "Site",
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["siteId"] = "0f8fad5b-d9cb-469f-a165-70867728950e", ["year"] = "2026" },
        Cursor = "c:42",
        DefinitionHash = "def1",
        AuthHash = "auth1"
    };

    // ---------------------------------------------------------------- server → client

    public static G9DtHelloResult HelloResult() => new()
    {
        Epoch = Epoch,
        ServerTimeUtc = new DateTime(2026, 10, 5, 12, 34, 56, 789, DateTimeKind.Utc).AddTicks(1234),
        ProtocolVersion = 1,
        Tables =
        [
            new G9DtNegotiatedTable
            {
                TableId = 3,
                Name = "Operation.Pot",
                KeyColumn = "Id",
                KeyType = G9ESyncColumnType.Guid,
                Columns =
                [
                    new G9DtColumnModel { Name = "RidgeId", Type = G9ESyncColumnType.Guid, Nullable = true },
                    new G9DtColumnModel { Name = "Code", Type = G9ESyncColumnType.String, Nullable = false },
                    new G9DtColumnModel { Name = "Weight", Type = G9ESyncColumnType.Decimal, Nullable = true }
                ],
                ComputedColumns = ["Weight"],
                ReadOnlyColumns = ["Code"],
                MissingColumns = ["Ghost"],
                TypeMismatchColumns = [new G9DtColumnTypeMismatch { Name = "Count", ClientType = G9ESyncColumnType.Int32, ServerType = G9ESyncColumnType.Int64 }],
                Direction = G9ESyncDirection.DownloadOnly,
                ConflictPolicy = G9EConflictPolicy.ColumnMerge
            }
        ],
        UnknownTables = ["Nope.Table"],
        Definitions =
        [
            new G9DtSubscriptionDefinition
            {
                Name = "Site",
                Parameters = [new G9DtParameterDefinition { Name = "siteId", Type = G9ESyncColumnType.Guid, Required = true, IsList = false }],
                DependsOn = [new G9DtSubscriptionDependency { Name = "Sites", Required = false }],
                Tables = ["Operation.Pot"],
                Ephemeral = true,
                TtlSeconds = 60,
                Heavy = true,
                MoveOutPolicy = G9EMoveOutPolicy.Keep,
                ReconcileIntervalMinutes = 30,
                DefinitionHash = "def1"
            }
        ],
        Subscriptions = [new G9DtSubscriptionState { Key = "Site?siteId=0f8fad5b-d9cb-469f-a165-70867728950e", Action = G9ESubscriptionAction.Reconcile, DefinitionHash = "def1", AuthHash = "auth1", Message = "changed" }],
        MaxPushRows = 5000,
        MaxPushBytes = 4_000_000_000
    };

    public static G9DtPushResult PushResult() => new()
    {
        BatchId = 12,
        AlreadyApplied = true,
        Accepted = [new G9DtAcceptedRows { TableId = 3, Keys = ["k1", "k2"], Versions = [1, 9_007_199_254_740_993], Deleted = [false, true] }],
        Rejections =
        [
            new G9DtRowRejection
            {
                TableId = 3, Key = "k3", Code = G9SyncErrorCodes.RejectConstraint, Message = "duplicate", SqlState = "23505", Constraint = "UX_Pot_Code",
                Column = "Code", Details = "{\"a\":1}"
            }
        ],
        Conflicts =
        [
            new G9DtRowConflict { TableId = 3, Key = "k4", Code = G9SyncErrorCodes.ConflictColumns, ServerChangedMask = (1L << 61) | 5, PartiallyApplied = true },
            new G9DtRowConflict { TableId = 3, Key = "k5", Code = G9SyncErrorCodes.ConflictServerWins, ServerChangedMask = -1, PartiallyApplied = false }
        ],
        CanonicalFrames = [[0x47, 0x39, 0x52, 0x42, 1, 0, 4, 0, 0, 0, 0, 0, 0, 0]],
        CommandResults = [new G9DtCommandResult { CommandId = CommandId, Status = G9ECommandStatus.Rejected, Code = "APP-1", Message = "no", Result = "{\"id\":7}" }],
        Error = new G9DtSyncError { Code = G9SyncErrorCodes.RejectTooMany, Message = "too many", Retryable = false, Details = "max 10" }
    };

    public static G9DtPullPlan PullPlan() => new()
    {
        Token = "tok.en",
        Parts =
        [
            new G9DtPlannedPart { SubscriptionKey = "Site", TableId = 3, Kind = G9EPullPartKind.Delta, Rows = 123, EstimatedBytes = 5_000_000_000 },
            new G9DtPlannedPart
            {
                SubscriptionKey = "Farm?farmId=7", TableId = 0, Kind = G9EPullPartKind.SnapshotFile, Rows = 450_000, EstimatedBytes = 22_000_000,
                File = new G9DtPlannedFile
                {
                    FileId = "53c01b6fc8a629f20732c103c23758d0", SnapshotUtc = new DateTime(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc),
                    Tables = [new G9DtPlannedFileTable { TableId = 3, Rows = 300_000 }, new G9DtPlannedFileTable { TableId = 4, Rows = 150_000 }]
                }
            }
        ],
        TotalRows = 123,
        EstimatedBytes = 5_000_000_000,
        Cursors = [new G9DtSubscriptionCursor { Key = "Site", Cursor = "c:43", DefinitionHash = "def1", AuthHash = "auth1" }],
        Skipped = [new G9DtSubscriptionState { Key = "Other", Action = G9ESubscriptionAction.Forbidden, Message = "no" }]
    };

    public static G9DtSyncChunk FullChunk() => new()
    {
        Sequence = 3,
        Payload = [1, 2, 3, 250, 255],
        RowCount = 2,
        SubscriptionKey = "Site?siteId=0f8fad5b-d9cb-469f-a165-70867728950e",
        IsFinal = true,
        Continuation = new G9DtPullContinuation { PartIndex = 1, AfterKey = "k", NextSequence = 4 },
        ReconcileSummary = new G9DtReconcileSummary
        {
            Cursor = "c:44", DefinitionHash = "def1", AuthHash = "auth1", Mismatched = [new G9DtTableBuckets { TableId = 3, Buckets = [0, 255] }]
        },
        File = new G9DtFileSegment
        {
            FileId = "53c01b6fc8a629f20732c103c23758d0", Index = 2, Offset = 2_097_152, Length = 1_048_576,
            Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            Manifest = new G9DtSnapshotFileManifest
            {
                FileId = "53c01b6fc8a629f20732c103c23758d0", FormatVersion = 1, Bytes = 5_000_000_000, SegmentBytes = 1_048_576, Segments = 4769,
                Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Compression = "br",
                BuiltUtc = new DateTime(2026, 10, 12, 8, 31, 5, DateTimeKind.Utc),
                Tables = [new G9DtSnapshotFileTableInfo { TableId = 3, Name = "Pot", VersionTable = "g9snap_ver_3", Rows = 300_000 }]
            }
        }
    };

    public static G9DtSyncChunk EmptyChunk() => new();

    public static G9DtChangesAvailable Changes() => new() { Tables = ["Operation.Pot", "Base.Site"] };

    // ---------------------------------------------------------------- client → server

    public static G9DtHelloRequest HelloRequest() => new()
    {
        ClientId = ClientId,
        AppVersion = "6.0.1",
        ProtocolVersion = 1,
        Tables =
        [
            new G9DtClientTableModel
            {
                Name = "Operation.Pot", KeyColumn = "Id", KeyType = G9ESyncColumnType.Guid,
                Columns = [new G9DtColumnModel { Name = "Code", Type = G9ESyncColumnType.String, Nullable = false }, new G9DtColumnModel { Name = "Weight", Type = G9ESyncColumnType.Decimal, Nullable = true }]
            }
        ],
        Subscriptions = [Subscription()]
    };

    public static G9DtPushRequest PushRequest() => new()
    {
        ClientId = ClientId,
        BatchId = 77,
        Subscriptions = [Subscription()],
        Tables = [new G9DtTableColumns { TableId = 3, Columns = ["Code", "Weight"] }],
        Commands =
        [
            new G9DtCommand
            {
                CommandId = CommandId, Sequence = 5, Name = "Pot.Move", Version = 2, Payload = "{\"to\":\"r1\"}", CreatedUtcMs = 1_759_667_696_789,
                Touched = [new G9DtRowRef { TableId = 3, Key = "k1" }]
            }
        ],
        HeldTables = [new G9DtTableColumns { TableId = 4, Columns = ["Name"] }],
        FrameVersion = 2
    };

    /// <summary>The single item the TypeScript client streams with <see cref="PushRequest" />.</summary>
    public static G9DtSyncChunk PushChunk() => new() { Sequence = 0, Payload = [0x47, 0x39, 0x52, 0x42, 1, 0, 4, 0, 0, 0, 0, 0, 0, 0], RowCount = 0, IsFinal = true };

    public static G9DtPullRequest PullRequest() => new()
    {
        ClientId = ClientId,
        Subscriptions = [Subscription()],
        Tables = [new G9DtTableColumns { TableId = 3, Columns = ["Code"] }],
        Backfills = [new G9DtBackfillRequest { TableId = 3, Columns = ["Weight"] }],
        FrameVersion = 2,
        SnapshotFiles = new G9DtSnapshotFileRequest
        {
            FormatVersion = 1,
            Storage = new G9DtSnapshotFileStorage { DateTimeAsTicks = false, DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff", DecimalAsText = false },
            Tables =
            [
                new G9DtSnapshotFileTable
                {
                    TableId = 3, LocalName = "Pot", KeyColumn = "Id", KeyType = G9ESyncColumnType.Guid,
                    Columns = [new G9DtColumnModel { Name = "Code", Type = G9ESyncColumnType.String, Nullable = false }],
                    Indexes = [new G9DtSnapshotFileIndex { Name = "IX_Pot_Code", Columns = ["Code"], Unique = true }]
                }
            ],
            MinRows = 20_000,
            MaxBytes = 2_147_483_648,
            MaxFiles = 8
        }
    };

    public static G9DtPullContinuation Continuation() => new() { PartIndex = 2, AfterKey = "after", NextSequence = 9 };

    public static G9DtReconcileRequest ReconcileRequest()
    {
        var digest = new G9DtTableDigest { TableId = 3, ComputedColumns = ["Weight"] };
        digest.Digests[0] = long.MinValue;
        digest.Digests[1] = -1;
        digest.Digests[2] = 9_007_199_254_740_993;
        digest.Digests[255] = long.MaxValue;
        digest.Counts[0] = 1;
        digest.Counts[255] = 2_000_000;
        return new G9DtReconcileRequest { ClientId = ClientId, Subscription = Subscription(), Tables = [digest], FrameVersion = 2 };
    }

    public static G9DtFetchRowsRequest FetchRowsRequest() => new()
    {
        ClientId = ClientId, Subscription = Subscription(), TableId = 3, Keys = ["k1", "k2"], Columns = ["Code"], FrameVersion = 1
    };
}
