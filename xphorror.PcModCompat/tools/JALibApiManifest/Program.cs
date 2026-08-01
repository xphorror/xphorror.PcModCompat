using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using dnlib.DotNet;

namespace Xphorror.PcModCompat.Tools;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                return Usage();
            return args[0] switch
            {
                "generate" => Generate(args),
                "compare" => Compare(args),
                "verify" => Verify(args),
                _ => Usage()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Generate(string[] args)
    {
        if (args.Length != 4)
            return Usage();
        var manifest = JalibApiManifestScanner.Scan(args[1], args[3]);
        WriteJson(args[2], manifest);
        Console.WriteLine(
            $"JALib API manifest version={manifest.SourceVersion} " +
            $"types={manifest.Types.Count} members={manifest.Members.Count} -> {args[2]}");
        return 0;
    }

    private static int Compare(string[] args)
    {
        if (args.Length < 5)
            return Usage();
        var report = BuildCoverageReport(args);
        WriteJson(args[2], report);
        WriteCoverageSummary(report, args[2]);
        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 5)
            return Usage();
        var report = BuildCoverageReport(args);
        WriteJson(args[2], report);
        WriteCoverageSummary(report, args[2]);

        var failures = JalibApiCompatibilityGate.Validate(report);
        if (failures.Count == 0)
        {
            Console.WriteLine("JALib API gate passed at the v42/v44 satisfiable union maximum.");
            return 0;
        }

        foreach (var failure in failures)
            Console.Error.WriteLine($"JALib API gate failed: {failure}");
        return 3;
    }

    private static JalibApiCoverageReport BuildCoverageReport(string[] args)
    {
        var candidate = JalibApiManifestScanner.Scan(args[1], "candidate");
        var references = args[3..]
            .Select(ReadManifest)
            .ToArray();
        return JalibApiManifestScanner.Compare(candidate, references);
    }

    private static void WriteCoverageSummary(JalibApiCoverageReport report, string path)
        => Console.WriteLine(
            $"JALib API coverage types={report.TypeCoverage:P2} " +
            $"members={report.MemberCoverage:P2} missingTypes={report.MissingTypes.Count} " +
            $"missingMembers={report.MissingMembers.Count} -> {path}");

    private static JalibApiManifest ReadManifest(string path)
        => JsonSerializer.Deserialize<JalibApiManifest>(
               File.ReadAllText(path, Encoding.UTF8),
               JsonOptions()) ??
           throw new InvalidDataException($"Invalid JALib API manifest: {path}");

    private static void WriteJson<T>(string path, T value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(value, JsonOptions()),
            new UTF8Encoding(false));
    }

    private static JsonSerializerOptions JsonOptions()
        => new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage:\n" +
            "  JALibApiManifest generate <JALib.dll> <manifest.json> <version>\n" +
            "  JALibApiManifest compare <candidate.dll> <report.json> <manifest...>\n" +
            "  JALibApiManifest verify <candidate.dll> <report.json> <manifest...>");
        return 2;
    }
}

public static class JalibApiCompatibilityGate
{
    public const string AllowedMissingMember =
        "F|JALib.Core.Patch.ReversePatchType|AllCombine|static=True|" +
        "type=JALib.Core.Patch.ReversePatchType|literal=127|attrs=";

    public static IReadOnlyList<string> Validate(JalibApiCoverageReport report)
    {
        var failures = new List<string>();
        if (!report.ReferenceVersions.SequenceEqual(
                new[] { "1.0.0.42", "1.0.0.44" },
                StringComparer.Ordinal))
        {
            failures.Add(
                $"reference versions must be 1.0.0.42 and 1.0.0.44, got " +
                $"[{string.Join(", ", report.ReferenceVersions)}]");
        }
        if (report.RequiredTypeCount != 61 || report.RequiredMemberCount != 872)
        {
            failures.Add(
                $"reference union must contain 61 types and 872 members, got " +
                $"{report.RequiredTypeCount} types and {report.RequiredMemberCount} members");
        }
        if (report.MissingTypes.Count != 0)
            failures.Add($"unexpected missing types: {FormatItems(report.MissingTypes)}");
        if (!report.MissingMembers.SequenceEqual(
                new[] { AllowedMissingMember },
                StringComparer.Ordinal))
        {
            failures.Add(
                $"missing members must contain only the pinned v42 AllCombine literal; got " +
                FormatItems(report.MissingMembers));
        }
        return failures;
    }

    private static string FormatItems(IReadOnlyList<string> items)
    {
        const int limit = 5;
        var preview = string.Join("; ", items.Take(limit));
        return items.Count <= limit
            ? $"[{preview}]"
            : $"count={items.Count}, first={limit} [{preview}] (see JSON report for full list)";
    }
}

public static class JalibApiManifestScanner
{
    private static readonly HashSet<string> RequiredAttributeNames = new(StringComparer.Ordinal)
    {
        "System.AttributeUsageAttribute",
        "System.FlagsAttribute",
        "System.ObsoleteAttribute",
        "System.Runtime.CompilerServices.ExtensionAttribute",
        "System.ParamArrayAttribute"
    };

    public static JalibApiManifest Scan(string assemblyPath, string sourceVersion)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        using var module = ModuleDefMD.Load(fullPath);
        var types = module.GetTypes()
            .Where(IsVisibleType)
            .Select(TypeKey)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var members = module.GetTypes()
            .Where(IsVisibleType)
            .SelectMany(MemberKeys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new JalibApiManifest
        {
            SchemaVersion = 1,
            SourceVersion = sourceVersion,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))),
            Types = types,
            Members = members
        };
    }

    public static JalibApiCoverageReport Compare(
        JalibApiManifest candidate,
        IReadOnlyList<JalibApiManifest> references)
    {
        var requiredTypes = references
            .SelectMany(manifest => manifest.Types)
            .ToHashSet(StringComparer.Ordinal);
        var requiredMembers = references
            .SelectMany(manifest => manifest.Members)
            .ToHashSet(StringComparer.Ordinal);
        var candidateTypes = candidate.Types.ToHashSet(StringComparer.Ordinal);
        var candidateMembers = candidate.Members.ToHashSet(StringComparer.Ordinal);
        var missingTypes = requiredTypes.Except(candidateTypes).Order(StringComparer.Ordinal).ToArray();
        var missingMembers = requiredMembers.Except(candidateMembers).Order(StringComparer.Ordinal).ToArray();
        return new JalibApiCoverageReport
        {
            SchemaVersion = 1,
            CandidateSha256 = candidate.SourceSha256,
            ReferenceVersions = references.Select(item => item.SourceVersion).ToArray(),
            RequiredTypeCount = requiredTypes.Count,
            PresentTypeCount = requiredTypes.Count - missingTypes.Length,
            RequiredMemberCount = requiredMembers.Count,
            PresentMemberCount = requiredMembers.Count - missingMembers.Length,
            MissingTypes = missingTypes,
            MissingMembers = missingMembers
        };
    }

    private static bool IsVisibleType(TypeDef type)
        => (type.IsPublic || type.IsNestedPublic || type.IsNestedFamily ||
            type.IsNestedFamilyOrAssembly) &&
           !type.Name.String.StartsWith('<') &&
           !HasAttribute(
               type.CustomAttributes,
               "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private static bool IsVisible(MethodDef method)
        => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static bool IsVisible(FieldDef field)
        => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static string TypeKey(TypeDef type)
        => $"T|{TypeName(type)}|kind={TypeKind(type)}|base={TypeName(type.BaseType)}" +
           $"|gp={GenericParameters(type.GenericParameters)}|attrs={Attributes(type.CustomAttributes)}";

    private static IEnumerable<string> MemberKeys(TypeDef type)
    {
        var owner = TypeName(type);
        foreach (var method in type.Methods.Where(IsVisible))
        {
            var parameters = method.Parameters
                .Where(parameter => !parameter.IsHiddenThisParameter && !parameter.IsReturnTypeParameter)
                .Select(parameter =>
                    $"{TypeName(parameter.Type)}" +
                    $"{(HasAttribute(parameter.ParamDef?.CustomAttributes, "System.ParamArrayAttribute") ? "..." : string.Empty)}")
                .ToArray();
            yield return $"M|{owner}|{method.Name}|static={method.IsStatic}" +
                         $"|ret={TypeName(method.ReturnType)}|args={string.Join(',', parameters)}" +
                         $"|gp={GenericParameters(method.GenericParameters)}" +
                         $"|attrs={Attributes(method.CustomAttributes)}";
        }

        foreach (var field in type.Fields.Where(IsVisible))
        {
            var literal = field.HasConstant
                ? Convert.ToString(field.Constant?.Value, System.Globalization.CultureInfo.InvariantCulture)
                : null;
            yield return $"F|{owner}|{field.Name}|static={field.IsStatic}|type={TypeName(field.FieldType)}" +
                         $"|literal={literal}|attrs={Attributes(field.CustomAttributes)}";
        }

        foreach (var property in type.Properties.Where(property =>
                     property.GetMethod != null && IsVisible(property.GetMethod) ||
                     property.SetMethod != null && IsVisible(property.SetMethod)))
        {
            var index = property.PropertySig?.Params.Select(TypeName) ?? [];
            var visibleGetter = property.GetMethod != null && IsVisible(property.GetMethod);
            var visibleSetter = property.SetMethod != null && IsVisible(property.SetMethod);
            yield return $"P|{owner}|{property.Name}|type={TypeName(property.PropertySig?.RetType)}" +
                         $"|index={string.Join(',', index)}|get={visibleGetter}" +
                         $"|set={visibleSetter}|attrs={Attributes(property.CustomAttributes)}";
        }

        foreach (var @event in type.Events.Where(@event =>
                     @event.AddMethod != null && IsVisible(@event.AddMethod) ||
                     @event.RemoveMethod != null && IsVisible(@event.RemoveMethod)))
        {
            var visibleAdd = @event.AddMethod != null && IsVisible(@event.AddMethod);
            var visibleRemove = @event.RemoveMethod != null && IsVisible(@event.RemoveMethod);
            yield return $"E|{owner}|{@event.Name}|type={TypeName(@event.EventType)}" +
                         $"|add={visibleAdd}|remove={visibleRemove}" +
                         $"|attrs={Attributes(@event.CustomAttributes)}";
        }
    }

    private static string GenericParameters(IList<GenericParam> parameters)
        => string.Join(';', parameters.Select(parameter =>
            $"{(int)parameter.Flags}:" +
            string.Join('&', parameter.GenericParamConstraints
                .Select(constraint => TypeName(constraint.Constraint))
                .Order(StringComparer.Ordinal))));

    private static string Attributes(CustomAttributeCollection attributes)
        => string.Join(',', attributes
            .Select(attribute => attribute.AttributeType.FullName)
            .Where(RequiredAttributeNames.Contains)
            .Order(StringComparer.Ordinal));

    private static bool HasAttribute(
        CustomAttributeCollection? attributes,
        string name)
        => attributes?.Any(attribute => attribute.AttributeType.FullName == name) == true;

    private static string TypeKind(TypeDef type)
        => type.IsInterface ? "interface" : type.IsEnum ? "enum" :
           type.IsValueType ? "struct" : type.IsDelegate ? "delegate" : "class";

    private static string TypeName(IType? type)
        => type?.FullName.Replace('/', '+') ?? "System.Void";
}

public sealed class JalibApiManifest
{
    public required int SchemaVersion { get; init; }
    public required string SourceVersion { get; init; }
    public required string SourceSha256 { get; init; }
    public required IReadOnlyList<string> Types { get; init; }
    public required IReadOnlyList<string> Members { get; init; }
}

public sealed class JalibApiCoverageReport
{
    public required int SchemaVersion { get; init; }
    public required string CandidateSha256 { get; init; }
    public required IReadOnlyList<string> ReferenceVersions { get; init; }
    public required int RequiredTypeCount { get; init; }
    public required int PresentTypeCount { get; init; }
    public required int RequiredMemberCount { get; init; }
    public required int PresentMemberCount { get; init; }
    public required IReadOnlyList<string> MissingTypes { get; init; }
    public required IReadOnlyList<string> MissingMembers { get; init; }
    public double TypeCoverage => RequiredTypeCount == 0 ? 1d : (double)PresentTypeCount / RequiredTypeCount;
    public double MemberCoverage => RequiredMemberCount == 0 ? 1d : (double)PresentMemberCount / RequiredMemberCount;
}
