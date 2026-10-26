namespace G9SyncData.Tests.Shared;

/// <summary>
///     Dotmim.Sync 1.3.x's SQLite objects exactly as they exist on an AgriPad device - copied from a device database (emulator, 2026-09-17,
///     table <c>Block</c>) with only the table name substituted. Note the DOUBLE underscore in the trigger names; the tracking table has one.
/// </summary>
public static class DotmimDeviceSql
{
    /// <summary>Dotmim's "downloaded" marker: a row applied from the server carries a non-null scope id.</summary>
    public const string ServerScopeId = "00000000-0000-0000-0000-000000000000";

    public const string ScopeTables = """
        CREATE TABLE [scope_info](
                                sync_scope_name text NOT NULL,
                                sync_scope_schema text NULL,
                                sync_scope_setup text NULL,
                                sync_scope_version text NULL,
                                sync_scope_last_clean_timestamp integer NULL,
                                sync_scope_properties text NULL,
                                CONSTRAINT PKey_scope_info PRIMARY KEY(sync_scope_name));
        CREATE TABLE [scope_info_client](
                                sync_scope_id blob NOT NULL,
                                sync_scope_name text NOT NULL,
                                sync_scope_hash text NOT NULL,
                                sync_scope_parameters text NULL,
                                scope_last_sync_timestamp integer NULL,
                                scope_last_server_sync_timestamp integer NULL,
                                scope_last_sync_duration integer NULL,
                                scope_last_sync datetime NULL,
                                sync_scope_errors text NULL,
                                sync_scope_properties text NULL,
                                CONSTRAINT PKey_scope_info_client PRIMARY KEY(sync_scope_id, sync_scope_name, sync_scope_hash));
        """;

    /// <summary>A scope client row with its last-sync stamp (Dotmim's <c>yyyyMMddHHmmssfff</c> integer).</summary>
    public static string ScopeClient(string scope, long? lastSyncStamp) =>
        $"INSERT INTO [scope_info_client] (sync_scope_id, sync_scope_name, sync_scope_hash, scope_last_sync_timestamp) " +
        $"VALUES ('9AF3754A-8F82-4DD5-A881-3952343EA23D', '{scope}', 'hash-{scope}', {(lastSyncStamp?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL")});";

    /// <summary>
    ///     A scope's <c>scope_info</c> row listing <paramref name="tables" />, its setup serialised as Dotmim 1.3.17 does it
    ///     (<c>SerializersFactory.JsonSerializerFactory</c>, checked 2026-10-26): <c>{"tbls":[{"tn":"Tree","sn":"Operation",…}]}</c>.
    /// </summary>
    public static string Scope(string scope, params string[] tables) =>
        "INSERT INTO [scope_info] (sync_scope_name, sync_scope_setup) VALUES ('" + scope + "', '{\"tbls\":[" +
        string.Join(",", tables.Select(t => "{\"tn\":\"" + t + "\",\"sn\":\"Operation\",\"cols\":[],\"ecols\":[],\"icols\":[],\"shadtblcols\":[]}")) +
        "],\"fils\":[],\"secols\":[]}');";

    /// <summary>The tracking table, its index and the three triggers Dotmim provisions for <paramref name="table" /> keyed by <c>[Id]</c>.</summary>
    public static string Capture(string table) => $$"""
        CREATE TABLE [{{table}}_tracking] (
        [Id] text NOT NULL COLLATE NOCASE,
        [update_scope_id] [text] NULL COLLATE NOCASE,
        [timestamp] [integer] NULL,
        [sync_row_is_tombstone] [integer] NOT NULL default(0),
        [last_change_datetime] [datetime] NULL,
         PRIMARY KEY ([Id]));
        CREATE INDEX [{{table}}_tracking_timestamp_index] ON [{{table}}_tracking] (
        	 [timestamp] ASC
        	,[update_scope_id] ASC
        	,[sync_row_is_tombstone] ASC
        	,[Id] ASC
        );
        CREATE TRIGGER [{{table}}__delete_trigger] AFTER DELETE ON [{{table}}]

        BEGIN
        	INSERT OR REPLACE INTO [{{table}}_tracking] (
        		[Id]
        		,[update_scope_id]
        		,[timestamp]
        		,[sync_row_is_tombstone]
        		,[last_change_datetime]
        	)
        	VALUES (
        		old.[Id]
        		,NULL
        		,replace(strftime('%Y%m%d%H%M%f', 'now'), '.', '')
        		,1
        		,datetime('now')
        	);
        END;
        CREATE TRIGGER [{{table}}__insert_trigger] AFTER INSERT ON [{{table}}]

        BEGIN
        -- If row was deleted before, it already exists, so just make an update
        	INSERT OR REPLACE INTO [{{table}}_tracking] (
        		[Id]
        		,[update_scope_id]
        		,[timestamp]
        		,[sync_row_is_tombstone]
        		,[last_change_datetime]
        	)
        	VALUES (
        		new.[Id]
        		,NULL
        		,replace(strftime('%Y%m%d%H%M%f', 'now'), '.', '')
        		,0
        		,datetime('now')
        	);
        END;
        CREATE TRIGGER [{{table}}__update_trigger] AFTER UPDATE ON [{{table}}]

        Begin
        	UPDATE [{{table}}_tracking]
        	SET [update_scope_id] = NULL -- scope id is always NULL when update is made locally
        		,[timestamp] = replace(strftime('%Y%m%d%H%M%f', 'now'), '.', '')
        		,[last_change_datetime] = datetime('now')
        	Where [{{table}}_tracking].[Id] = new.[Id];
        	INSERT OR IGNORE INTO [{{table}}_tracking] (
        		[Id]
        		,[update_scope_id]
        		,[timestamp]
        		,[sync_row_is_tombstone]
        		,[last_change_datetime]
        	)
        	SELECT
        		new.[Id]
        		,NULL
        		,replace(strftime('%Y%m%d%H%M%f', 'now'), '.', '')
        		,0
        		,datetime('now')
        	WHERE (SELECT COUNT(*) FROM [{{table}}_tracking] WHERE [Id]=new.[Id])=0
        ;
        End;
        """;

    /// <summary>What Dotmim's apply leaves behind for rows it downloaded (or uploaded) at <paramref name="stamp" />: not a local change.</summary>
    public static string MarkSynced(string table, long stamp) =>
        $"UPDATE [{table}_tracking] SET update_scope_id = '{ServerScopeId}', [timestamp] = {stamp.ToString(System.Globalization.CultureInfo.InvariantCulture)};";
}
