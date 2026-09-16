namespace G9SyncData;

/// <summary>
///     Marks an entity class as a synced client table. The <c>G9SyncData.SourceGenerators</c> generator turns every
///     marked class into a <c>G9CSyncClientTable</c> at compile time, so the client model is built with no runtime
///     reflection (NativeAOT-safe).
/// </summary>
/// <param name="serverTable">The server-side logical table name, e.g. <c>Operation.Pot</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class G9AttrSyncTableAttribute(string serverTable) : Attribute
{
    /// <summary>Server-side logical table name (<c>schema.table</c>).</summary>
    public string ServerTable { get; } = serverTable;

    /// <summary>Local SQLite table name. Default: the sqlite-net <c>[Table]</c> name, else the class name.</summary>
    public string? LocalName { get; set; }
}

/// <summary>Marks the primary-key property. Optional when a property is named <c>Id</c> or carries <c>[PrimaryKey]</c>.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class G9AttrSyncKeyAttribute : Attribute;

/// <summary>Excludes a property from the synced column set.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class G9AttrSyncIgnoreAttribute : Attribute;

/// <summary>Overrides the column name, type or nullability the generator infers from a property.</summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class G9AttrSyncColumnAttribute : Attribute
{
    /// <summary>Column name. Default: the sqlite-net <c>[Column]</c> name, else the property name.</summary>
    public string? Name { get; set; }

    /// <summary>Portable type. <see cref="G9ESyncColumnType.Unspecified" /> infers from the property type.</summary>
    public G9ESyncColumnType Type { get; set; }

    /// <summary>Nullability. <see cref="G9ESyncNullability.Infer" /> uses nullable annotations.</summary>
    public G9ESyncNullability Nullability { get; set; }
}

/// <summary>Declares a secondary index the library creates on the local table (Dotmim created none, P-17).</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class G9AttrSyncIndexAttribute(params string[] columns) : Attribute
{
    public string[] Columns { get; } = columns;

    /// <summary>Index name. Default: <c>IX_{Table}_{Col1}_{Col2}</c>.</summary>
    public string? Name { get; set; }

    public bool Unique { get; set; }
}
