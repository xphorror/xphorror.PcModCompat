using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xphorror.PcModCompat;

public sealed class PcCompatRuntimeRuleBundle
{
    public const string CurrentFormatVersion = "mvp-fixed-op-v3";

    public string FormatVersion { get; init; } = CurrentFormatVersion;
    public required string ModId { get; init; }
    public required string RecipeId { get; init; }
    public required string Compatibility { get; init; }
    public ulong RequiredCapabilities { get; init; }
    public IReadOnlyList<PcCompatRuntimeTarget> Targets { get; init; } = Array.Empty<PcCompatRuntimeTarget>();

    public static PcCompatRuntimeRuleBundle FromReport(PcCompatRecipeCompileReport report)
    {
        foreach (var rule in report.Rules)
            ValidateTargetIdentity(rule);

        var targets = report.Rules
            .GroupBy(
                RuntimeTargetKey.FromRule,
                rule => rule)
            .OrderBy(group => group.Key.AssemblyName, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Namespace, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TypeName, StringComparer.Ordinal)
            .ThenBy(group => group.Key.MethodName, StringComparer.Ordinal)
            .ThenBy(group => group.Key.IsStatic)
            .ThenBy(group => group.Key.GenericArity)
            .ThenBy(group => group.Key.ReturnType, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ParameterTypes, StringComparer.Ordinal)
            .Select((group, index) => new PcCompatRuntimeTarget
            {
                Id = index + 1,
                AssemblyName = NormalizeAssemblyNameForOutput(group.First().TargetAssemblyName),
                Namespace = group.Key.Namespace,
                TypeName = group.Key.TypeName,
                MethodName = group.Key.MethodName,
                IsStatic = group.Key.IsStatic,
                GenericArity = group.Key.GenericArity,
                ReturnType = group.Key.ReturnType,
                ParameterTypes = group.First().TargetParameterTypes.ToArray(),
                ParamCount = group.First().TargetParameterTypes.Count,
                AbiKind = InferAbiKind(
                    group.Key.TypeName,
                    group.Key.MethodName,
                    group.Key.IsStatic,
                    group.Key.ReturnType,
                    group.First().TargetParameterTypes),
                Rules = group
                    .OrderBy(rule => rule.Stage)
                    .ThenBy(rule => rule.Id, StringComparer.Ordinal)
                    .Select(ToRuntimeRule)
                    .ToArray()
            })
            .ToArray();

        return new PcCompatRuntimeRuleBundle
        {
            ModId = report.ModId,
            RecipeId = report.RecipeId,
            Compatibility = report.Compatibility,
            RequiredCapabilities = (ulong)report.RequiredCapabilities,
            Targets = targets
        };
    }

    public static string Serialize(PcCompatRuntimeRuleBundle bundle)
        => JsonSerializer.Serialize(bundle, JsonOptions);

    public static PcCompatRuntimeRuleBundle? Deserialize(string json)
        => JsonSerializer.Deserialize<PcCompatRuntimeRuleBundle>(json, JsonOptions);

    private static void ValidateTargetIdentity(PcCompatCompiledRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.TargetAssemblyName) ||
            string.IsNullOrWhiteSpace(rule.TargetType) ||
            string.IsNullOrWhiteSpace(rule.TargetMethod) ||
            string.IsNullOrWhiteSpace(rule.TargetReturnType))
        {
            throw new InvalidOperationException($"Rule {rule.Id} has an incomplete target identity.");
        }
        if (rule.TargetGenericArity < 0)
            throw new InvalidOperationException($"Rule {rule.Id} has a negative generic arity.");
        if (rule.TargetParameterTypes is null || rule.TargetParameterTypes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Rule {rule.Id} has an empty target parameter type.");
        if (rule.ParamCount is { } paramCount && paramCount != rule.TargetParameterTypes.Count)
        {
            throw new InvalidOperationException(
                $"Rule {rule.Id} paramCount={paramCount} does not match its complete signature ({rule.TargetParameterTypes.Count}).");
        }
    }

    private static PcCompatRuntimeRule ToRuntimeRule(PcCompatCompiledRule rule)
        => new()
        {
            Id = rule.Id,
            FeatureId = rule.FeatureId,
            Stage = rule.Stage.ToString(),
            StageCode = (int)rule.Stage,
            Op = rule.Op.ToString(),
            OpCode = (int)rule.Op,
            RequiredCapabilities = (ulong)rule.RequiredCapabilities,
            DefaultEnabled = rule.DefaultEnabled,
            Source = rule.Source
        };

    private static string InferAbiKind(
        string typeName,
        string methodName,
        bool isStatic,
        string returnType,
        IReadOnlyList<string> parameterTypes)
    {
        var paramCount = parameterTypes.Count;
        if (typeName == "scnGame" && methodName == "Play" && paramCount == 2)
            return "InstanceBool2";

        if (typeName == "scrPlayer" && methodName == "Hit" && paramCount == 1)
            return "InstanceBool1";

        if (typeName == "scrPlayer" && methodName == "HitInputEvent" && paramCount == 2)
            return "InstanceBoolBoolInt";

        if (typeName == "scrPlayer" && methodName == "Die" && paramCount == 4)
            return "InstanceVoidBoolBoolPtrBool";

        if (typeName == "scrMisc" && methodName == "GetHitMargin" && paramCount == 6)
            return "StaticIntFloatFloatBoolFloatFloatDouble";

        if (typeName == "scrFloor" && methodName == "SetTileColor" && paramCount == 1)
            return "InstanceVoidColor1";

        if (typeName == "PlanetRenderer" &&
            methodName is "SetPlanetColor" or "SetCoreColor" or "SetTailColor" or "SetRingColor" or "SetFaceColor" &&
            paramCount == 1)
        {
            return "InstanceVoidColor1";
        }

        if (typeName == "PlanetRenderer" && methodName == "SetColor" && paramCount == 2)
            return "InstanceVoidPtrBool";

        if (typeName == "scrUIController" && methodName == "WipeToBlack" && paramCount == 3)
            return "InstanceVoid3";

        if (typeName == "scrMistakesManager" && methodName == "SetPlayerCount" && paramCount == 1)
            return "StaticVoid1";

        if (typeName == "scrMarginTracker" && methodName == "AddHit" && paramCount == 1)
            return "InstanceVoidInt1";

        if (typeName == "scrPlanet" && methodName == "MoveToNextFloor" && paramCount == 3)
            return "InstanceVoidPtrFloatInt";

        if (isStatic)
        {
            return returnType == "System.Void" &&
                   parameterTypes.Count == 1 &&
                   IsGp32(parameterTypes[0])
                ? "StaticVoid1"
                : "Unknown";
        }

        if (returnType != "System.Void")
            return "Unknown";

        return parameterTypes.Count switch
        {
            0 => "InstanceVoid0",
            1 when IsGp32(parameterTypes[0]) => "InstanceVoidInt1",
            1 => "InstanceVoid1",
            2 when IsGp32(parameterTypes[0]) && parameterTypes[1] == "System.Boolean" => "InstanceVoidIntBool",
            3 when IsReferenceLike(parameterTypes[0]) && parameterTypes[1] == "System.Single" && IsGp32(parameterTypes[2])
                => "InstanceVoidPtrFloatInt",
            3 when parameterTypes.All(IsReferenceLike) => "InstanceVoid3",
            _ => "Unknown"
        };
    }

    private static bool IsGp32(string typeName)
        => typeName is "System.Boolean" or "System.Byte" or "System.SByte" or
            "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Char" or
            "HitMargin" or "InputEventState";

    private static bool IsReferenceLike(string typeName)
        => !IsGp32(typeName) &&
           typeName is not ("System.Int64" or "System.UInt64" or "System.Single" or "System.Double" or "System.Decimal");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string NormalizeAssemblyNameForOutput(string value)
    {
        var normalized = value.Trim();
        return normalized.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^4]
            : normalized;
    }

    private readonly record struct RuntimeTargetKey(
        string AssemblyName,
        string Namespace,
        string TypeName,
        string MethodName,
        bool IsStatic,
        int GenericArity,
        string ReturnType,
        string ParameterTypes)
    {
        public static RuntimeTargetKey FromRule(PcCompatCompiledRule rule)
            => new(
                NormalizeAssemblyName(rule.TargetAssemblyName),
                rule.TargetNamespace,
                rule.TargetType,
                rule.TargetMethod,
                rule.TargetIsStatic,
                rule.TargetGenericArity,
                rule.TargetReturnType,
                string.Join("\0", rule.TargetParameterTypes));

        private static string NormalizeAssemblyName(string value)
            => NormalizeAssemblyNameForOutput(value).ToUpperInvariant();
    }
}

public sealed class PcCompatRuntimeTarget
{
    public int Id { get; init; }
    public string AssemblyName { get; init; } = "Assembly-CSharp";
    public string Namespace { get; init; } = string.Empty;
    public required string TypeName { get; init; }
    public required string MethodName { get; init; }
    public bool IsStatic { get; init; }
    public int GenericArity { get; init; }
    public required string ReturnType { get; init; }
    public IReadOnlyList<string> ParameterTypes { get; init; } = Array.Empty<string>();
    public int? ParamCount { get; init; }
    public string AbiKind { get; init; } = "Unknown";
    public IReadOnlyList<PcCompatRuntimeRule> Rules { get; init; } = Array.Empty<PcCompatRuntimeRule>();
}

public sealed class PcCompatRuntimeRule
{
    public required string Id { get; init; }
    public required string FeatureId { get; init; }
    public required string Stage { get; init; }
    public int StageCode { get; init; }
    public required string Op { get; init; }
    public int OpCode { get; init; }
    public ulong RequiredCapabilities { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public string Source { get; init; } = "recipe";
}
