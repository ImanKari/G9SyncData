# 13 — Source generator

`G9SyncData.SourceGenerators` turns entity classes into the client model at compile time, so the app never builds it by
reflection (ADR-0003). Source: `src/G9SyncData.SourceGenerators`.

## Wiring

```xml
<!-- package -->
<PackageReference Include="G9SyncData.SourceGenerators" PrivateAssets="all" />
<!-- or project reference inside this tree -->
<ProjectReference Include="..\..\src\G9SyncData.SourceGenerators\G9SyncData.SourceGenerators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

The project must also reference `G9SyncData.Client`; otherwise `G9SG007` warns and nothing is generated.

## Use

```csharp
[Table("Pot")]                                   // sqlite-net, honoured
[G9AttrSyncTable("Operation.Pot")]
[G9AttrSyncIndex("SiteId", "Code", Unique = true)]
public sealed class PotEntity : BaseEntity        // [PrimaryKey] string Id inherited from the base
{
    public Guid SiteId { get; set; }
    [G9AttrSyncColumn(Nullability = G9ESyncNullability.NotNull)] public string Code { get; set; } = "";
    [Column("Order")] public int SortOrder { get; set; }
    [Ignore] public string Transient { get; set; } = "";
}

services.AddG9SyncDataClient(G9SyncGeneratedModel.Model);
// or combine with hand-written tables:
var model = G9SyncGeneratedModel.Configure(new G9CSyncClientModelBuilder()).Table(...).Build();
```

Generated: `internal static class G9SyncData.Generated.G9SyncGeneratedModel` with `Model` (built once) and
`Configure(builder)`. Inspect it with `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>`.

## Rules

| Aspect | Rule |
|---|---|
| Local table name | `LocalName` → sqlite-net `[Table(name)]` → class name |
| Properties | public instance properties with public getter and setter, base classes first; an override keeps its base position |
| Excluded | `[G9AttrSyncIgnore]`, sqlite-net `[Ignore]`, getter-only properties |
| Key | `[G9AttrSyncKey]` → sqlite-net `[PrimaryKey]` → property named `Id`; type Guid/String/Int32/Int64 (override with `[G9AttrSyncColumn(Type = …)]`, e.g. a `string` key holding GUIDs → `Guid`) |
| Column name | `[G9AttrSyncColumn(Name)]` → sqlite-net `[Column(name)]` → property name |
| Nullability | `[G9AttrSyncColumn(Nullability)]` → sqlite-net `[NotNull]` → value types by `Nullable<T>`; **reference types nullable** (ADR-0016) |
| Indexes | `[G9AttrSyncIndex(columns…)]` on the class; sqlite-net `[Indexed]` / `[Unique]` on properties (unnamed → one-column index; the same `Name` → one composite index ordered by `Order`). Default name `IX_{Local}_{Col}_{Col}` |

## Type inference

| C# | Portable type |
|---|---|
| `bool` | Boolean |
| `byte`, `sbyte`, `short`, `ushort`, `int`, enums (≤ 32-bit underlying) | Int32 |
| `uint`, `long`, enums with 64-bit underlying | Int64 |
| `float`, `double` | Double |
| `decimal` | Decimal |
| `string` | String |
| `Guid`, `DateTime`, `DateTimeOffset`, `TimeSpan` | same name |
| `byte[]` | Bytes |
| anything else (`ulong`, `char`, `Uri`, collections…) | `G9SG002` error |

## Diagnostics

| Id | Severity | Meaning |
|---|---|---|
| G9SG001 | Error | no key found |
| G9SG002 | Error | property type cannot be synced |
| G9SG003 | Error | more than 62 synced columns |
| G9SG004 | Error | unsupported key type |
| G9SG005 | Error | `[G9AttrSyncTable]` without a server table name |
| G9SG006 | Error | server or local table mapped by two classes |
| G9SG007 | Warning | `G9SyncData.Client` not referenced |

New diagnostics go into `AnalyzerReleases.Unshipped.md` (release tracking is enforced) and messages are single sentences
without abbreviations (`RS1032`).

## Pipeline notes

Uses `ForAttributeWithMetadataName`; everything passed between stages is a value-equatable record (`EquatableArray<T>`),
so editing an unrelated file does not re-run emission. Proven by `tests/G9SyncData.Tests/Generator/GeneratedModelTests.cs`,
whose entities are generated while the test assembly compiles.
