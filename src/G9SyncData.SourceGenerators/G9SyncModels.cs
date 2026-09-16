using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace G9SyncData.SourceGenerators;

// Everything flowing through the incremental pipeline is a value-equatable record, so an edit that does not change a
// synced class does not re-run emission.

internal sealed record GenerationTarget(string AssemblyName, bool HasClientModel);

internal sealed record TableModel(
    string TypeName,
    string ServerTable,
    string LocalName,
    string KeyColumn,
    string KeyType,
    EquatableArray<ColumnModel> Columns,
    EquatableArray<IndexModel> Indexes,
    LocationInfo? Location,
    EquatableArray<DiagnosticInfo> Diagnostics);

internal sealed record ColumnModel(string Name, string Type, bool Nullable);

internal sealed record IndexModel(string? Name, bool Unique, EquatableArray<string> Columns);

internal sealed record DiagnosticInfo(string Id, EquatableArray<string> Arguments, LocationInfo? Location);

internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return location?.SourceTree is null ? null : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }
}

/// <summary>An immutable array with value equality (arrays compare by reference, which defeats incremental caching).</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(IEnumerable<T> items) => _items = items.ToArray();

    public int Count => _items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other)
    {
        if (Count != other.Count) return false;
        for (var i = 0; i < Count; i++)
        {
            if (!_items![i].Equals(other._items![i])) return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in this) hash = unchecked((hash * 31) + item.GetHashCode());
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
