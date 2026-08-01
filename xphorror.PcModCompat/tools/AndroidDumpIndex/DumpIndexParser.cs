using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Xphorror.PcModCompat.AndroidDumpIndex;

internal sealed partial class DumpIndexParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "const", "volatile",
        "virtual", "override", "abstract", "sealed", "extern", "unsafe", "new", "partial", "async"
    };
    private static readonly HashSet<string> ParameterModifiers = new(StringComparer.Ordinal)
    {
        "ref", "out", "in", "params", "this"
    };

    private readonly string _path;
    private readonly List<DumpParseWarning> _warnings = new();
    private int _warningCount;

    public DumpIndexParser(string path)
    {
        _path = path;
    }

    public int WarningCount => _warningCount;
    public IReadOnlyList<DumpParseWarning> Warnings => _warnings;

    public IReadOnlyList<DumpImageRecord> ReadImages()
    {
        var images = new List<DumpImageRecord>();
        using var reader = OpenReader();
        while (reader.ReadLine() is { } line)
        {
            var match = ImageRegex().Match(line);
            if (match.Success)
            {
                images.Add(new DumpImageRecord(
                    int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture),
                    match.Groups["assembly"].Value.Trim()));
                continue;
            }

            if (line.StartsWith("// Dll :", StringComparison.Ordinal))
                break;
        }
        return images;
    }

    public IEnumerable<DumpTypeRecord> ReadTypes()
    {
        using var reader = OpenReader();
        string assemblyName = string.Empty;
        string namespaze = string.Empty;
        TypeBuilder? current = null;
        Section section = Section.None;
        DumpMethodAuditRecord? pendingAudit = null;
        var lineNumber = 0;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (line.StartsWith("// Dll :", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    Warn(lineNumber, "Encountered a new assembly marker before the current type closed.", line);
                    yield return current.Build();
                    current = null;
                }
                assemblyName = line[8..].Trim();
                section = Section.None;
                pendingAudit = null;
                continue;
            }

            if (line.StartsWith("// Namespace:", StringComparison.Ordinal))
            {
                namespaze = line[13..].Trim();
                continue;
            }

            if (current is null)
            {
                if (!TryParseTypeDeclaration(line, assemblyName, namespaze, lineNumber, out current))
                    continue;
                section = Section.None;
                pendingAudit = null;
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed == "// Fields")
            {
                section = Section.Fields;
                pendingAudit = null;
                continue;
            }
            if (trimmed == "// Properties")
            {
                section = Section.Properties;
                pendingAudit = null;
                continue;
            }
            if (trimmed == "// Methods")
            {
                section = Section.Methods;
                pendingAudit = null;
                continue;
            }

            if (trimmed == "}")
            {
                yield return current.Build();
                current = null;
                section = Section.None;
                pendingAudit = null;
                continue;
            }

            if (section == Section.Methods)
            {
                var addressMatch = MethodAddressRegex().Match(trimmed);
                if (addressMatch.Success)
                {
                    pendingAudit = new DumpMethodAuditRecord
                    {
                        RvaHex = NormalizeHex(addressMatch.Groups["rva"].Value),
                        VaHex = NormalizeHex(addressMatch.Groups["va"].Value),
                        FileOffsetHex = NormalizeHex(addressMatch.Groups["offset"].Value)
                    };
                    continue;
                }

                if (TryParseMethod(trimmed, lineNumber, pendingAudit, out var method))
                {
                    current.Methods.Add(method);
                    pendingAudit = null;
                }
                continue;
            }

            if (section == Section.Fields && TryParseField(trimmed, lineNumber, out var field))
            {
                current.Fields.Add(field);
                continue;
            }

            if (section == Section.Properties && TryParseProperty(trimmed, lineNumber, out var property))
                current.Properties.Add(property);
        }

        if (current is not null)
        {
            Warn(lineNumber, "The final type did not have a closing brace.", current.RawDeclaration);
            yield return current.Build();
        }
    }

    private StreamReader OpenReader()
        => new(_path, StrictUtf8, true, 64 * 1024);

    private static bool TryParseTypeDeclaration(
        string line,
        string assemblyName,
        string namespaze,
        int lineNumber,
        out TypeBuilder? builder)
    {
        builder = null;
        var match = TypeDeclarationRegex().Match(line.Trim());
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value;
        var fullName = string.IsNullOrEmpty(namespaze) ? name : namespaze + "." + name;
        builder = new TypeBuilder
        {
            AssemblyName = assemblyName,
            Namespace = namespaze,
            Name = name,
            FullName = fullName,
            Kind = match.Groups["kind"].Value,
            Modifiers = SplitWords(match.Groups["prefix"].Value),
            BaseTypes = SplitTopLevel(match.Groups["bases"].Value, ','),
            RawDeclaration = line.Trim(),
            Line = lineNumber
        };
        return true;
    }

    private static bool TryParseField(string line, int lineNumber, out DumpFieldRecord field)
    {
        field = null!;
        var match = FieldRegex().Match(line);
        if (!match.Success)
            return false;

        var declaration = RemoveTopLevelInitializer(match.Groups["declaration"].Value.Trim());
        var words = SplitWords(declaration);
        var firstType = Array.FindIndex(words, word => !MemberModifiers.Contains(word));
        if (firstType < 0 || firstType + 1 >= words.Length)
            return false;

        field = new DumpFieldRecord
        {
            Name = words[^1],
            TypeName = string.Join(' ', words[firstType..^1]),
            IsStatic = words.Contains("static", StringComparer.Ordinal) || words.Contains("const", StringComparer.Ordinal),
            OffsetHex = NormalizeHex(match.Groups["offset"].Value),
            RawDeclaration = line,
            Line = lineNumber
        };
        return true;
    }

    private static bool TryParseProperty(string line, int lineNumber, out DumpPropertyRecord property)
    {
        property = null!;
        var match = PropertyRegex().Match(line);
        if (!match.Success)
            return false;

        var words = SplitWords(match.Groups["head"].Value);
        var firstType = Array.FindIndex(words, word => !MemberModifiers.Contains(word));
        if (firstType < 0 || firstType + 1 >= words.Length)
            return false;

        var accessors = match.Groups["accessors"].Value;
        property = new DumpPropertyRecord
        {
            Name = words[^1],
            TypeName = string.Join(' ', words[firstType..^1]),
            IsStatic = words.Contains("static", StringComparer.Ordinal),
            HasGetter = Regex.IsMatch(accessors, @"\bget\s*;", RegexOptions.CultureInvariant),
            HasSetter = Regex.IsMatch(accessors, @"\bset\s*;", RegexOptions.CultureInvariant),
            RawDeclaration = line,
            Line = lineNumber
        };
        return true;
    }

    private static bool TryParseMethod(
        string line,
        int lineNumber,
        DumpMethodAuditRecord? audit,
        out DumpMethodRecord method)
    {
        method = null!;
        var match = MethodRegex().Match(line);
        if (!match.Success)
            return false;

        var headWords = SplitWords(match.Groups["head"].Value);
        var firstType = Array.FindIndex(headWords, word => !MemberModifiers.Contains(word));
        if (firstType < 0 || firstType + 1 >= headWords.Length)
            return false;

        var returnType = string.Join(' ', headWords[firstType..^1]);
        var name = headWords[^1];
        var genericArity = ParseGenericArity(name);
        var parameters = ParseParameters(match.Groups["parameters"].Value);
        var isStatic = headWords.Contains("static", StringComparer.Ordinal);
        var identity = $"{(isStatic ? "static" : "instance")} {returnType} {name}" +
                       $"({string.Join(", ", parameters.Select(parameter => FormatParameterIdentity(parameter)))})";

        method = new DumpMethodRecord
        {
            Name = name,
            ReturnType = returnType,
            IsStatic = isStatic,
            IsGeneric = genericArity > 0,
            GenericArity = genericArity,
            Parameters = parameters,
            SymbolicIdentity = identity,
            RawDeclaration = line,
            Line = lineNumber,
            Audit = audit
        };
        return true;
    }

    private static IReadOnlyList<DumpParameterRecord> ParseParameters(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<DumpParameterRecord>();

        var result = new List<DumpParameterRecord>();
        foreach (var part in SplitTopLevel(raw, ','))
        {
            var declaration = RemoveTopLevelInitializer(part.Trim());
            var words = SplitWords(declaration);
            if (words.Length == 0)
                continue;

            string? modifier = null;
            var start = 0;
            if (ParameterModifiers.Contains(words[0]))
            {
                modifier = words[0];
                start = 1;
            }

            var remaining = words[start..];
            if (remaining.Length == 0)
                continue;

            var hasName = remaining.Length > 1;
            result.Add(new DumpParameterRecord
            {
                Modifier = modifier,
                TypeName = hasName ? string.Join(' ', remaining[..^1]) : remaining[0],
                Name = hasName ? remaining[^1] : null,
                RawDeclaration = part.Trim()
            });
        }
        return result;
    }

    private static string FormatParameterIdentity(DumpParameterRecord parameter)
        => string.IsNullOrEmpty(parameter.Modifier)
            ? parameter.TypeName
            : parameter.Modifier + " " + parameter.TypeName;

    private static int ParseGenericArity(string name)
    {
        var marker = name.LastIndexOf('`');
        return marker >= 0 && int.TryParse(name[(marker + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var arity)
            ? arity
            : 0;
    }

    private static string RemoveTopLevelInitializer(string declaration)
    {
        var index = FindTopLevelCharacter(declaration, '=');
        return index < 0 ? declaration : declaration[..index].TrimEnd();
    }

    private static int FindTopLevelCharacter(string value, char target)
    {
        var round = 0;
        var square = 0;
        var angle = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    round++;
                    break;
                case ')':
                    round--;
                    break;
                case '[':
                    square++;
                    break;
                case ']':
                    square--;
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    angle--;
                    break;
                default:
                    if (value[index] == target && round == 0 && square == 0 && angle == 0)
                        return index;
                    break;
            }
        }
        return -1;
    }

    private static string[] SplitTopLevel(string value, char separator)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();

        var result = new List<string>();
        var start = 0;
        var round = 0;
        var square = 0;
        var angle = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    round++;
                    break;
                case ')':
                    round--;
                    break;
                case '[':
                    square++;
                    break;
                case ']':
                    square--;
                    break;
                case '<':
                    angle++;
                    break;
                case '>':
                    angle--;
                    break;
                default:
                    if (value[index] == separator && round == 0 && square == 0 && angle == 0)
                    {
                        result.Add(value[start..index].Trim());
                        start = index + 1;
                    }
                    break;
            }
        }
        result.Add(value[start..].Trim());
        return result.Where(item => item.Length != 0).ToArray();
    }

    private static string[] SplitWords(string value)
        => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? NormalizeHex(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? "0x" + trimmed[2..].ToLowerInvariant()
            : "0x" + trimmed.ToLowerInvariant();
    }

    private void Warn(int line, string message, string rawLine)
    {
        _warningCount++;
        if (_warnings.Count < 100)
            _warnings.Add(new DumpParseWarning(line, message, rawLine));
    }

    private enum Section
    {
        None,
        Fields,
        Properties,
        Methods
    }

    private sealed class TypeBuilder
    {
        public required string AssemblyName { get; init; }
        public required string Namespace { get; init; }
        public required string Name { get; init; }
        public required string FullName { get; init; }
        public required string Kind { get; init; }
        public required string[] Modifiers { get; init; }
        public required string[] BaseTypes { get; init; }
        public required string RawDeclaration { get; init; }
        public int Line { get; init; }
        public List<DumpFieldRecord> Fields { get; } = new();
        public List<DumpPropertyRecord> Properties { get; } = new();
        public List<DumpMethodRecord> Methods { get; } = new();

        public DumpTypeRecord Build()
            => new()
            {
                AssemblyName = AssemblyName,
                Namespace = Namespace,
                Name = Name,
                FullName = FullName,
                Kind = Kind,
                Modifiers = Modifiers,
                BaseTypes = BaseTypes,
                RawDeclaration = RawDeclaration,
                Line = Line,
                Fields = Fields.ToArray(),
                Properties = Properties.ToArray(),
                Methods = Methods.ToArray()
            };
    }

    [GeneratedRegex(@"^// Image\s+(?<index>\d+):\s*(?<assembly>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^(?<prefix>.*?)\b(?<kind>class|struct|interface|enum)\s+(?<name>[^\s:{]+)(?:\s*:\s*(?<bases>.+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(@"^(?<declaration>.+?);\s*//\s*(?<offset>0x[0-9A-Fa-f]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"^(?<head>.+?)\s*\{(?<accessors>.+)\}$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyRegex();

    [GeneratedRegex(@"^(?<head>.+?)\((?<parameters>.*)\)\s*(?:\{\s*\}|;)$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodRegex();

    [GeneratedRegex(@"^// RVA:\s*(?<rva>0x[0-9A-Fa-f]+)(?:\s+Offset:\s*(?<offset>0x[0-9A-Fa-f]+))?\s+VA:\s*(?<va>0x[0-9A-Fa-f]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex MethodAddressRegex();
}
