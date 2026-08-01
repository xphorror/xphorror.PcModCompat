using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat.AndroidDumpIndex;

internal sealed record DumpImageRecord(int Index, string AssemblyName);

internal sealed class DumpTypeRecord
{
    public required string AssemblyName { get; init; }
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string Kind { get; init; }
    public IReadOnlyList<string> Modifiers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BaseTypes { get; init; } = Array.Empty<string>();
    public required string RawDeclaration { get; init; }
    public int Line { get; init; }
    public IReadOnlyList<DumpFieldRecord> Fields { get; init; } = Array.Empty<DumpFieldRecord>();
    public IReadOnlyList<DumpPropertyRecord> Properties { get; init; } = Array.Empty<DumpPropertyRecord>();
    public IReadOnlyList<DumpMethodRecord> Methods { get; init; } = Array.Empty<DumpMethodRecord>();
}

internal sealed class DumpFieldRecord
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public bool IsStatic { get; init; }
    public string? OffsetHex { get; init; }
    public required string RawDeclaration { get; init; }
    public int Line { get; init; }
}

internal sealed class DumpPropertyRecord
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public bool IsStatic { get; init; }
    public bool HasGetter { get; init; }
    public bool HasSetter { get; init; }
    public required string RawDeclaration { get; init; }
    public int Line { get; init; }
}

internal sealed class DumpMethodRecord
{
    public required string Name { get; init; }
    public required string ReturnType { get; init; }
    public bool IsStatic { get; init; }
    public bool IsGeneric { get; init; }
    public int GenericArity { get; init; }
    public IReadOnlyList<DumpParameterRecord> Parameters { get; init; } = Array.Empty<DumpParameterRecord>();
    public required string SymbolicIdentity { get; init; }
    public required string RawDeclaration { get; init; }
    public int Line { get; init; }
    public DumpMethodAuditRecord? Audit { get; init; }
}

internal sealed class DumpParameterRecord
{
    public string? Name { get; init; }
    public required string TypeName { get; init; }
    public string? Modifier { get; init; }
    public required string RawDeclaration { get; init; }
}

internal sealed class DumpMethodAuditRecord
{
    public string AddressUse => "audit_only";
    public string? RvaHex { get; init; }
    public string? VaHex { get; init; }
    public string? FileOffsetHex { get; init; }
}

internal sealed record DumpParseWarning(int Line, string Message, string RawLine);

internal sealed class DumpIndexSummary
{
    public int TypesRead { get; set; }
    public int TypesWritten { get; set; }
    public int FieldsWritten { get; set; }
    public int PropertiesWritten { get; set; }
    public int MethodsWritten { get; set; }
    public int MethodsWithAuditAddress { get; set; }
    public int ParseWarningCount { get; set; }
}

internal sealed class DumpIndexSource
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public required string Sha256 { get; init; }
    public string Encoding => "utf-8-strict";
    public string RuntimeAddressPolicy => "metadata_only";
    public string DumpAddressPolicy => "audit_only";
}

internal sealed class DumpIndexFilter
{
    public string? AssemblyName { get; init; }
    public IReadOnlyList<string> TypeNames { get; init; } = Array.Empty<string>();
}

internal sealed class DumpTypeCatalog
{
    public string FormatVersion => "xphorror.android-il2cpp-type-catalog.v1";
    public required DumpIndexSource Source { get; init; }
    public required IReadOnlyList<DumpTypeCatalogRecord> Types { get; init; }
    public int TypeCount => Types.Count;
}

internal sealed record DumpTypeCatalogRecord(
    string AssemblyName,
    string Namespace,
    string Name,
    string FullName,
    string Kind,
    int Line);
