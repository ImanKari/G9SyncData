// Stand-ins for the sqlite-net-pcl attributes (same namespace, names and shapes), so the generator's sqlite-net support is
// tested without referencing sqlite-net.
// ReSharper disable once CheckNamespace
namespace SQLite;

[AttributeUsage(AttributeTargets.Class)]
public sealed class TableAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ColumnAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class PrimaryKeyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class IgnoreAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class NotNullAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public class IndexedAttribute : Attribute
{
    public IndexedAttribute()
    {
    }

    public IndexedAttribute(string name, int order)
    {
        Name = name;
        Order = order;
    }

    public string? Name { get; set; }
    public int Order { get; set; }
    public virtual bool Unique { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class UniqueAttribute : IndexedAttribute
{
    public override bool Unique
    {
        get => true;
        set { }
    }
}
