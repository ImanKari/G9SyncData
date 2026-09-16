using System.Linq;
using Microsoft.CodeAnalysis;

namespace G9SyncData.SourceGenerators;

internal static class G9SyncDiagnostics
{
    private const string Category = "G9SyncData";

    public static readonly DiagnosticDescriptor NoKey = new("G9SG001", "Synced table has no key",
        "Synced table '{0}' has no key: add [G9AttrSyncKey], a sqlite-net [PrimaryKey], or an 'Id' property", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedType = new("G9SG002", "Property type cannot be synced",
        "'{0}.{1}' has type '{2}', which G9SyncData cannot sync; mark it [G9AttrSyncIgnore] or give it an explicit [G9AttrSyncColumn] type", Category,
        DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor TooManyColumns = new("G9SG003", "Too many synced columns",
        "'{0}' has {1} synced columns; the maximum is 62 because the local change mask is 64 bits", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnsupportedKey = new("G9SG004", "Key type is not supported",
        "Key '{0}.{1}' has type '{2}'; keys must be Guid, String, Int32 or Int64", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NoServerTable = new("G9SG005", "Server table name is missing",
        "[G9AttrSyncTable] on '{0}' needs the server table name in schema-dot-table form", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Duplicate = new("G9SG006", "Table mapped twice",
        "{0} table '{1}' is mapped by more than one class: {2}", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MissingClient = new("G9SG007", "G9SyncData.Client is not referenced",
        "[G9AttrSyncTable] classes were found but G9SyncData.Client is not referenced, so no client model was generated", Category,
        DiagnosticSeverity.Warning, true);

    private static readonly DiagnosticDescriptor[] All = [NoKey, UnsupportedType, TooManyColumns, UnsupportedKey, NoServerTable, Duplicate, MissingClient];

    public static bool IsError(string id) => All.First(d => d.Id == id).DefaultSeverity == DiagnosticSeverity.Error;

    public static Diagnostic Create(DiagnosticInfo info) =>
        Diagnostic.Create(All.First(d => d.Id == info.Id), info.Location?.ToLocation(), info.Arguments.Cast<object>().ToArray());
}
