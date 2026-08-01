using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Xphorror.PcModCompat;

public sealed class PcCompatUiLoweringIssue
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? Method { get; init; }
    public int? IlOffset { get; init; }
    public string Severity { get; init; } = "warning";
}

public sealed class PcCompatUiGraphLoweringResult
{
    public IReadOnlyList<PcCompatUiObjectNode> ObjectGraph { get; init; } = Array.Empty<PcCompatUiObjectNode>();
    public IReadOnlyList<PcCompatUiResourceBinding> ResourceBindings { get; init; } =
        Array.Empty<PcCompatUiResourceBinding>();
    public IReadOnlyList<PcCompatUiLifecycleProgram> LifecyclePrograms { get; init; } = Array.Empty<PcCompatUiLifecycleProgram>();
    public IReadOnlyList<PcCompatUiLoweringIssue> Issues { get; init; } = Array.Empty<PcCompatUiLoweringIssue>();
    public int RootCount { get; init; }
    public int CandidateCount { get; init; }
    public int AcceptedCandidateCount { get; init; }

    public bool HasGraph => ObjectGraph.Count != 0 && LifecyclePrograms.Count != 0;
}

/// <summary>
/// Lowers the small, declarative part of a PC HUD constructor into the native
/// object-graph recipe. This class intentionally never loads or executes a
/// MOD assembly. It is a structural IL interpreter with a bounded UI opcode
/// catalog, not a general managed runtime.
/// </summary>
public static class PcCompatUiGraphLowerer
{
    private const int MaxReachableMethods = 2048;
    private const int MaxCallDepth = 16;
    private const int MaxInstructionsPerMethod = 1024;
    private const string UnityGameObject = "UnityEngine.GameObject";
    private const string UnityObject = "UnityEngine.Object";
    private const string UnityComponent = "UnityEngine.Component";
    private const string UnityTransform = "UnityEngine.Transform";
    private const string UnityRectTransform = "UnityEngine.RectTransform";
    private const string UnityVector2 = "UnityEngine.Vector2";
    private const string UnityVector3 = "UnityEngine.Vector3";
    private const string UnityColor = "UnityEngine.Color";
    private const string UnityCanvas = "UnityEngine.Canvas";
    private const string UnityCanvasScaler = "UnityEngine.UI.CanvasScaler";
    private const string UnityContentSizeFitter = "UnityEngine.UI.ContentSizeFitter";
    private const string UnityGraphic = "UnityEngine.UI.Graphic";
    private const string UnityImage = "UnityEngine.UI.Image";
    private const string UnityRawImage = "UnityEngine.UI.RawImage";
    private const string TmpText = "TMPro.TextMeshProUGUI";
    private const string TmpTextBase = "TMPro.TMP_Text";
    private const string UnityCanvasRenderer = "UnityEngine.CanvasRenderer";

    public static PcCompatUiGraphLoweringResult Lower(
        PcModManifest manifest,
        PcCompatStaticPatchScanReport staticScan)
    {
        var issues = new List<PcCompatUiLoweringIssue>();
        var paths = staticScan.AssembliesScanned
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return Result(issues, 0, 0, 0);
        }

        using var index = new AssemblyIndex(paths, issues);
        var resources = ResourceCatalog.Load(manifest, issues);
        var roots = FindLifecycleRoots(manifest, index);
        if (roots.Count == 0)
        {
            issues.Add(new PcCompatUiLoweringIssue
            {
                Code = "NoUiLifecycleRoot",
                Message = "No manifest lifecycle method could be resolved for UI discovery.",
                Severity = "info"
            });
            return Result(issues, 0, 0, 0);
        }

        var reachable = DiscoverReachableMethods(roots, index, issues);
        var relevant = FindUiRelevantMethods(reachable, index);
        var candidates = relevant
            .Where(method => method.Name == ".ctor" && method.ParameterTypes.Count == 0)
            .OrderBy(method => method.DisplayName, StringComparer.Ordinal)
            .ToArray();

        var graph = new GraphBuilder();
        var accepted = 0;
        foreach (var candidate in candidates)
        {
            var checkpoint = graph.Capture();
            var evaluator = new Evaluator(index, relevant, graph, resources, issues);
            try
            {
                var instance = Value.Managed(
                    "object:" + candidate.DisplayName,
                    candidate.DeclaringType);
                evaluator.Execute(candidate, instance, Array.Empty<Value>(),
                    "root:" + candidate.DisplayName, 0);
                accepted++;
            }
            catch (LoweringAbortException ex)
            {
                graph.Restore(checkpoint);
                issues.Add(new PcCompatUiLoweringIssue
                {
                    Code = ex.Code,
                    Message = ex.Message,
                    Method = candidate.DisplayName,
                    IlOffset = ex.Offset
                });
            }
        }

        var finalized = graph.FinalizeGraph(issues);
        if (finalized.Nodes.Count == 0)
        {
            if (candidates.Length != 0)
            {
                issues.Add(new PcCompatUiLoweringIssue
                {
                    Code = "UiGraphEmpty",
                    Message = "UI candidates were found, but none produced a complete supported object graph."
                });
            }
            return Result(issues, 0, candidates.Length, accepted);
        }

        var lifecycles = BuildLifecyclePrograms(finalized.Nodes, manifest.Id);
        return new PcCompatUiGraphLoweringResult
        {
            ObjectGraph = finalized.Nodes,
            ResourceBindings = finalized.ResourceBindings,
            LifecyclePrograms = lifecycles,
            Issues = issues,
            RootCount = finalized.Nodes.Count(node => node.ParentId == 0),
            CandidateCount = candidates.Length,
            AcceptedCandidateCount = accepted
        };
    }

    private static PcCompatUiGraphLoweringResult Result(
        List<PcCompatUiLoweringIssue> issues,
        int roots,
        int candidates,
        int accepted)
        => new()
        {
            Issues = issues,
            RootCount = roots,
            CandidateCount = candidates,
            AcceptedCandidateCount = accepted
        };

    private static List<MethodDef> FindLifecycleRoots(PcModManifest manifest, AssemblyIndex index)
    {
        var roots = new List<MethodDef>();
        if (!string.IsNullOrWhiteSpace(manifest.EntryMethod))
        {
            var split = manifest.EntryMethod.LastIndexOf('.');
            if (split > 0)
            {
                var type = NormalizeTypeName(manifest.EntryMethod[..split]);
                var method = manifest.EntryMethod[(split + 1)..];
                roots.AddRange(index.Find(type, method));
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.JAModClassName))
        {
            var type = NormalizeTypeName(manifest.JAModClassName);
            foreach (var method in new[] { ".ctor", "OnSetup", "OnEnable", "Start" })
                roots.AddRange(index.Find(type, method));
        }

        return roots
            .GroupBy(method => method.Identity, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static HashSet<MethodDef> DiscoverReachableMethods(
        IReadOnlyList<MethodDef> roots,
        AssemblyIndex index,
        List<PcCompatUiLoweringIssue> issues)
    {
        var reachable = new HashSet<MethodDef>();
        var pending = new Queue<MethodDef>(roots);
        while (pending.Count != 0 && reachable.Count < MaxReachableMethods)
        {
            var method = pending.Dequeue();
            if (!reachable.Add(method) || method.Body == null)
                continue;

            IReadOnlyList<PcCompatIlInstruction> instructions;
            try
            {
                instructions = PcCompatIlDecoder.Decode(method.Body);
            }
            catch (Exception ex)
            {
                issues.Add(new PcCompatUiLoweringIssue
                {
                    Code = "ReachabilityDecodeFailed",
                    Message = $"{ex.GetType().Name}: {ex.Message}",
                    Method = method.DisplayName
                });
                continue;
            }

            foreach (var instruction in instructions)
            {
                if (instruction.OpCode != OpCodes.Call &&
                    instruction.OpCode != OpCodes.Callvirt &&
                    instruction.OpCode != OpCodes.Newobj)
                    continue;

                var resolved = index.Resolve(method.Context, instruction.MetadataToken);
                if (resolved != null && !reachable.Contains(resolved))
                    pending.Enqueue(resolved);
            }
        }

        if (pending.Count != 0)
        {
            issues.Add(new PcCompatUiLoweringIssue
            {
                Code = "ReachabilityLimit",
                Message = $"UI lifecycle reachability exceeded {MaxReachableMethods} methods."
            });
        }

        return reachable;
    }

    private static HashSet<MethodDef> FindUiRelevantMethods(
        IReadOnlyCollection<MethodDef> reachable,
        AssemblyIndex index)
    {
        var relevant = new HashSet<MethodDef>();
        foreach (var method in reachable)
        {
            if (method.Body == null)
                continue;
            IReadOnlyList<PcCompatIlInstruction> instructions;
            try
            {
                instructions = PcCompatIlDecoder.Decode(method.Body);
            }
            catch
            {
                continue;
            }

            if (instructions.Any(instruction => IsDirectUiSeed(method.Context.Reader, instruction)))
                relevant.Add(method);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var method in reachable)
            {
                if (relevant.Contains(method) || method.Body == null)
                    continue;
                IReadOnlyList<PcCompatIlInstruction> instructions;
                try
                {
                    instructions = PcCompatIlDecoder.Decode(method.Body);
                }
                catch
                {
                    continue;
                }
                if (instructions.Any(instruction =>
                {
                    if (instruction.OpCode != OpCodes.Call &&
                        instruction.OpCode != OpCodes.Callvirt &&
                        instruction.OpCode != OpCodes.Newobj)
                        return false;
                    var target = index.Resolve(method.Context, instruction.MetadataToken);
                    return target != null && relevant.Contains(target);
                }))
                {
                    relevant.Add(method);
                    changed = true;
                }
            }
        }

        return relevant;
    }

    private static bool IsDirectUiSeed(MetadataReader reader, PcCompatIlInstruction instruction)
    {
        if (instruction.OpCode == OpCodes.Newobj)
        {
            var identity = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
            return identity.DeclaringType == UnityGameObject;
        }
        if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
            return false;

        var identityCall = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
        if (identityCall.IsEmpty)
            return false;
        if (identityCall.Name == "AddComponent" ||
            identityCall.Name == "GetComponent" ||
            identityCall.Name == "SetParent" ||
            identityCall.Name == "SetActive" ||
            identityCall.Name == "DontDestroyOnLoad")
            return true;
        return identityCall.Name.StartsWith("set_", StringComparison.Ordinal) &&
               IsKnownUiType(identityCall.DeclaringType);
    }

    private static bool IsKnownUiType(string type)
        => type is UnityRectTransform or UnityCanvas or UnityCanvasScaler or UnityContentSizeFitter or UnityImage or
            UnityRawImage or UnityGraphic or TmpText or TmpTextBase or UnityTransform or UnityGameObject;

    private static IReadOnlyList<PcCompatUiLifecycleProgram> BuildLifecyclePrograms(
        IReadOnlyList<PcCompatUiObjectNode> nodes,
        string modId)
    {
        var roots = nodes.Where(node => node.ParentId == 0).OrderBy(node => node.Id).ToArray();
        if (roots.Length == 0)
            return Array.Empty<PcCompatUiLifecycleProgram>();

        var usedRuleIds = new HashSet<uint>();
        uint NextRuleId(string suffix)
        {
            var candidate = StableId(modId + "|ui|" + suffix);
            if (candidate == 0)
                candidate = 1;
            while (!usedRuleIds.Add(candidate))
                ++candidate;
            return candidate;
        }

        var programs = new List<PcCompatUiLifecycleProgram>
        {
            new()
            {
                Id = "ui.graph.ensure",
                RuntimeRuleId = NextRuleId("ensure"),
                Trigger = PcCompatUiLifecycleTrigger.BundleLoad,
                ClockDomain = PcCompatUiClockDomain.Realtime,
                InstructionBudget = 16,
                CommandType = (uint)PcCompatPresentationCommandType.EnsureGraph,
                TargetId = roots[0].Id,
                DeferredRetryDelayNs = 5_000_000,
                Instructions = new[]
                {
                    new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.Return)
                }
            }
        };

        foreach (var root in roots)
        {
            programs.Add(new PcCompatUiLifecycleProgram
            {
                Id = "ui.graph.visibility." + root.Id,
                RuntimeRuleId = NextRuleId("visibility|" + root.Id),
                Trigger = PcCompatUiLifecycleTrigger.OverlayStateChanged,
                ClockDomain = PcCompatUiClockDomain.Realtime,
                InstructionBudget = 16,
                CommandType = (uint)PcCompatPresentationCommandType.SetActive,
                TargetId = root.Id,
                DeferredRetryDelayNs = 5_000_000,
                Instructions = new[]
                {
                    new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.LoadOverlayVisible, Destination: 0),
                    new PcCompatNativeVmInstruction(PcCompatNativeVmOpcode.Return)
                }
            });
        }

        return programs;
    }

    private static uint StableId(string value)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return hash;
        }
    }

    private static long FloatBits(double value)
        => BitConverter.SingleToInt32Bits((float)value);

    private static string NormalizeTypeName(string value)
        => value.Trim().Replace('/', '+');

    private enum ValueKind
    {
        Unknown,
        Null,
        Integer,
        Float,
        String,
        Node,
        Component,
        Managed,
        Vector2,
        Vector3,
        Color,
        Reference,
        Resource
    }

    private sealed class Value
    {
        public ValueKind Kind { get; private init; }
        public string TypeName { get; private init; } = string.Empty;
        public string Key { get; private init; } = string.Empty;
        public string Text { get; private init; } = string.Empty;
        public long Integer { get; private init; }
        public double Number { get; private init; }
        public double X { get; private init; }
        public double Y { get; private init; }
        public double Z { get; private init; }
        public double W { get; private init; }
        public PcCompatResourceBinding? ResourceBinding { get; private init; }

        public static Value Unknown(string type = "") => new() { Kind = ValueKind.Unknown, TypeName = type };
        public static Value Null() => new() { Kind = ValueKind.Null };
        public static Value Int(long value, string type = "System.Int32") => new() { Kind = ValueKind.Integer, Integer = value, TypeName = type };
        public static Value Float(double value) => new() { Kind = ValueKind.Float, Number = value, TypeName = "System.Single" };
        public static Value String(string value) => new() { Kind = ValueKind.String, Text = value, TypeName = "System.String" };
        public static Value Node(string key) => new() { Kind = ValueKind.Node, Key = key, TypeName = UnityGameObject };
        public static Value Component(string key, string type) => new() { Kind = ValueKind.Component, Key = key, TypeName = type };
        public static Value Managed(string key, string type) => new() { Kind = ValueKind.Managed, Key = key, TypeName = type };
        public static Value Vector2(double x, double y) => new() { Kind = ValueKind.Vector2, X = x, Y = y, TypeName = UnityVector2 };
        public static Value Vector3(double x, double y, double z) => new() { Kind = ValueKind.Vector3, X = x, Y = y, Z = z, TypeName = UnityVector3 };
        public static Value Color(double r, double g, double b, double a) => new() { Kind = ValueKind.Color, X = r, Y = g, Z = b, W = a, TypeName = UnityColor };
        public static Value Reference(string key, Value? fallback = null) => new() { Kind = ValueKind.Reference, Key = key, TypeName = fallback?.TypeName ?? string.Empty };
        public static Value Resource(PcCompatResourceBinding binding) => new()
        {
            Kind = ValueKind.Resource,
            TypeName = binding.ExpectedType,
            ResourceBinding = binding
        };

        public bool IsUi => Kind is ValueKind.Node or ValueKind.Component;
    }

    private sealed class ResourceCatalog
    {
        private readonly IReadOnlyList<PcCompatResourceBinding> _bindings;

        private ResourceCatalog(IReadOnlyList<PcCompatResourceBinding> bindings)
        {
            _bindings = bindings;
        }

        public static ResourceCatalog Load(
            PcModManifest manifest,
            List<PcCompatUiLoweringIssue> issues)
        {
            var path = Path.Combine(manifest.FolderPath, ".pccompat", "resource_recipe.bin");
            if (!File.Exists(path))
                return new ResourceCatalog(Array.Empty<PcCompatResourceBinding>());
            if (!PcCompatResourceRecipe.TryRead(path, out var document, out var error) ||
                !PcCompatResourceRecipe.TryValidateDocument(document, manifest.Id, out error))
            {
                issues.Add(new PcCompatUiLoweringIssue
                {
                    Code = "UiResourceRecipeRejected",
                    Message = error ?? "resource recipe validation failed",
                    Severity = "warning"
                });
                return new ResourceCatalog(Array.Empty<PcCompatResourceBinding>());
            }

            return new ResourceCatalog(document.Bindings
                .Where(binding =>
                    binding.Confidence.Equals("Proven", StringComparison.OrdinalIgnoreCase) ||
                    binding.Confidence == "1")
                .ToArray());
        }

        public bool TryResolve(
            PcCompatFieldIdentity field,
            string fieldType,
            out PcCompatResourceBinding binding)
        {
            binding = null!;
            var exact = _bindings.Where(candidate =>
                    EffectiveSourceFieldIdentity(candidate)
                        .Equals(field.DisplayName, StringComparison.Ordinal) &&
                    ResourceTypeMatches(candidate.ExpectedType, fieldType))
                .ToArray();
            if (exact.Length == 1)
            {
                binding = exact[0];
                return true;
            }
            if (exact.Length > 1)
                return false;

            // resource-recipe-v1 originally had no sourceFieldIdentity. Keep a
            // strict legacy bridge: field name, asset name and type must all
            // agree and the result must be unique.
            var legacy = _bindings.Where(candidate =>
                    candidate.SourceFieldIdentity.Length == 0 &&
                    candidate.AssetName.Equals(field.Name, StringComparison.OrdinalIgnoreCase) &&
                    ResourceTypeMatches(candidate.ExpectedType, fieldType))
                .ToArray();
            if (legacy.Length != 1)
                return false;
            binding = legacy[0];
            return true;
        }

        private static string EffectiveSourceFieldIdentity(PcCompatResourceBinding binding)
        {
            if (binding.SourceFieldIdentity.Length != 0)
                return binding.SourceFieldIdentity;

            // Backward compatibility for recipes produced before the structured
            // sourceFieldIdentity member was added. The reason text has always
            // been emitted by our own compiler in this fixed shape.
            const string prefix = "IL ";
            const string assetMarker = " asset '";
            const string fieldMarker = "' -> field ";
            const string proofMarker = " (name/type proven) @ 0x";
            if (!binding.Reason.StartsWith(prefix, StringComparison.Ordinal))
                return string.Empty;
            var asset = binding.Reason.IndexOf(assetMarker, prefix.Length, StringComparison.Ordinal);
            var fieldEnd = binding.Reason.LastIndexOf(proofMarker, StringComparison.Ordinal);
            var field = fieldEnd > 0
                ? binding.Reason.LastIndexOf(fieldMarker, fieldEnd, StringComparison.Ordinal)
                : -1;
            if (asset <= prefix.Length || field <= asset)
                return string.Empty;
            var methodIdentity = binding.Reason[prefix.Length..asset];
            var methodSeparator = methodIdentity.LastIndexOf('.');
            var fieldStart = field + fieldMarker.Length;
            if (methodSeparator <= 0 || fieldEnd <= fieldStart)
                return string.Empty;
            return methodIdentity[..methodSeparator] + "." + binding.Reason[fieldStart..fieldEnd];
        }

        private static bool ResourceTypeMatches(string resourceType, string fieldType)
        {
            static string Simple(string value)
            {
                var normalized = value.Split(',', 2)[0].Trim();
                return normalized[(normalized.LastIndexOf('.') + 1)..];
            }

            var resource = Simple(resourceType);
            var field = Simple(fieldType);
            if (resource.Equals(field, StringComparison.OrdinalIgnoreCase))
                return true;
            return resource.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase) &&
                   field.Contains("FontAsset", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ResourceBindingDraft
    {
        public required PcCompatUiResourceTarget Target { get; init; }
        public required PcCompatResourceBinding Binding { get; init; }
    }

    private sealed class NodeDraft
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public string? ParentKey { get; set; }
        public PcCompatUiComponentMask Components { get; set; } = PcCompatUiComponentMask.RectTransform;
        public bool ExplicitActive { get; set; }
        public bool Active { get; set; } = true;
        public bool DontDestroy { get; set; }
        public (double X, double Y)? AnchorMin { get; set; }
        public (double X, double Y)? AnchorMax { get; set; }
        public (double X, double Y)? AnchoredPosition { get; set; }
        public (double X, double Y)? SizeDelta { get; set; }
        public List<PcCompatUiComponentOperation> Operations { get; } = new();
        public List<ResourceBindingDraft> Resources { get; } = new();

        public NodeDraft Clone()
        {
            var clone = new NodeDraft { Key = Key, Name = Name, ParentKey = ParentKey };
            clone.Components = Components;
            clone.ExplicitActive = ExplicitActive;
            clone.Active = Active;
            clone.DontDestroy = DontDestroy;
            clone.AnchorMin = AnchorMin;
            clone.AnchorMax = AnchorMax;
            clone.AnchoredPosition = AnchoredPosition;
            clone.SizeDelta = SizeDelta;
            clone.Operations.AddRange(Operations);
            clone.Resources.AddRange(Resources);
            return clone;
        }
    }

    private sealed class FinalizedGraph
    {
        public required IReadOnlyList<PcCompatUiObjectNode> Nodes { get; init; }
        public required IReadOnlyList<PcCompatUiResourceBinding> ResourceBindings { get; init; }
    }

    private sealed class GraphCheckpoint
    {
        public required Dictionary<string, NodeDraft> Nodes { get; init; }
        public required Dictionary<string, Value> Fields { get; init; }
    }

    private sealed class GraphBuilder
    {
        private readonly Dictionary<string, NodeDraft> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Value> _fields = new(StringComparer.Ordinal);

        public GraphCheckpoint Capture()
            => new()
            {
                Nodes = _nodes.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
                Fields = new Dictionary<string, Value>(_fields, StringComparer.Ordinal)
            };

        public void Restore(GraphCheckpoint checkpoint)
        {
            _nodes.Clear();
            foreach (var pair in checkpoint.Nodes)
                _nodes.Add(pair.Key, pair.Value);
            _fields.Clear();
            foreach (var pair in checkpoint.Fields)
                _fields.Add(pair.Key, pair.Value);
        }

        public NodeDraft GetOrAddNode(string key, string name)
        {
            if (_nodes.TryGetValue(key, out var existing))
                return existing;
            var node = new NodeDraft { Key = key, Name = name };
            _nodes.Add(key, node);
            return node;
        }

        public bool TryGetNode(string key, out NodeDraft node) => _nodes.TryGetValue(key, out node!);
        public Dictionary<string, Value> Fields => _fields;

        public FinalizedGraph FinalizeGraph(List<PcCompatUiLoweringIssue> issues)
        {
            var ordered = _nodes.Values.OrderBy(node => node.Key, StringComparer.Ordinal).ToArray();
            var ids = new Dictionary<string, uint>(StringComparer.Ordinal);
            uint next = 1;
            foreach (var node in ordered)
                ids[node.Key] = next++;

            var result = new List<PcCompatUiObjectNode>(ordered.Length);
            var resources = new List<PcCompatUiResourceBinding>();
            foreach (var node in ordered)
            {
                uint parentId = 0;
                if (node.ParentKey != null)
                {
                    if (!ids.TryGetValue(node.ParentKey, out parentId))
                    {
                        issues.Add(new PcCompatUiLoweringIssue
                        {
                            Code = "MissingUiParent",
                            Message = $"UI node {node.Name} references missing parent {node.ParentKey}.",
                            Severity = "error"
                        });
                        continue;
                    }
                }

                var flags = node.Active ? PcCompatUiObjectFlags.ActiveInitially : PcCompatUiObjectFlags.None;
                if (node.DontDestroy)
                    flags |= PcCompatUiObjectFlags.DontDestroyOnLoad;
                var operations = new List<PcCompatUiComponentOperation>(node.Operations);
                if (node.AnchorMin != null || node.AnchorMax != null)
                {
                    var min = node.AnchorMin ?? (0.0, 0.0);
                    var max = node.AnchorMax ?? (1.0, 1.0);
                    operations.Add(new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetAnchors,
                        Payload0 = FloatBits(min.X),
                        Payload1 = FloatBits(min.Y),
                        Payload2 = FloatBits(max.X),
                        Payload3 = FloatBits(max.Y)
                    });
                }
                if (node.AnchoredPosition != null || node.SizeDelta != null)
                {
                    var position = node.AnchoredPosition ?? (0.0, 0.0);
                    var size = node.SizeDelta ?? (0.0, 0.0);
                    operations.Add(new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetRect,
                        Payload0 = FloatBits(position.X),
                        Payload1 = FloatBits(position.Y),
                        Payload2 = FloatBits(size.X),
                        Payload3 = FloatBits(size.Y)
                    });
                }
                result.Add(new PcCompatUiObjectNode
                {
                    Id = ids[node.Key],
                    ParentId = parentId,
                    Name = node.Name,
                    Components = node.Components,
                    Flags = flags,
                    Initialization = operations.ToArray()
                });
                foreach (var resource in node.Resources.OrderBy(item => item.Target))
                {
                    resources.Add(new PcCompatUiResourceBinding
                    {
                        NodeId = ids[node.Key],
                        Target = resource.Target,
                        FeatureGroupId = resource.Binding.FeatureGroupId,
                        AssetName = resource.Binding.AssetName,
                        ExpectedType = resource.Binding.ExpectedType
                    });
                }
            }
            return new FinalizedGraph
            {
                Nodes = result,
                ResourceBindings = resources
            };
        }
    }

    private sealed class Evaluator
    {
        private readonly AssemblyIndex _index;
        private readonly IReadOnlySet<MethodDef> _relevant;
        private readonly GraphBuilder _graph;
        private readonly ResourceCatalog _resources;
        private readonly List<PcCompatUiLoweringIssue> _issues;
        private readonly HashSet<string> _activeCalls = new(StringComparer.Ordinal);

        public Evaluator(
            AssemblyIndex index,
            IReadOnlySet<MethodDef> relevant,
            GraphBuilder graph,
            ResourceCatalog resources,
            List<PcCompatUiLoweringIssue> issues)
        {
            _index = index;
            _relevant = relevant;
            _graph = graph;
            _resources = resources;
            _issues = issues;
        }

        public Value Execute(MethodDef method, Value instance, IReadOnlyList<Value> arguments, string path, int depth)
        {
            if (depth > MaxCallDepth)
                throw Abort("CallDepthLimit", $"UI helper call depth exceeded {MaxCallDepth}.", method, null);
            if (method.Body == null)
                return Value.Unknown(method.ReturnType);
            var callKey = method.Identity + "|" + path;
            if (!_activeCalls.Add(callKey))
                throw Abort("RecursiveUiHelper", "Recursive UI helper call is not supported.", method, null);

            try
            {
                IReadOnlyList<PcCompatIlInstruction> instructions;
                try
                {
                    instructions = PcCompatIlDecoder.Decode(method.Body);
                }
                catch (Exception ex)
                {
                    throw Abort("UiMethodDecodeFailed", $"{ex.GetType().Name}: {ex.Message}", method, null);
                }
                if (instructions.Count > MaxInstructionsPerMethod)
                    throw Abort("UiMethodTooLarge", $"UI method exceeds {MaxInstructionsPerMethod} instructions.", method, null);

                var locals = new Value[64];
                Array.Fill(locals, Value.Unknown());
                var stack = new List<Value>();
                var frameArgs = new List<Value>();
                if (!method.IsStatic)
                    frameArgs.Add(instance);
                frameArgs.AddRange(arguments);

                foreach (var instruction in instructions)
                {
                    if (instruction.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Throw)
                    {
                        // Straight-line constructors are the only accepted
                        // form. A branch after already-emitted UI is safe to
                        // truncate; a branch before it would make the graph
                        // conditional and is rejected by the caller.
                        if (HasUiSeedAfter(instructions, instruction.Offset, method.Context.Reader))
                            throw Abort("ConditionalUiConstruction", "UI construction is controlled by a non-constant branch.", method, instruction.Offset);
                        break;
                    }

                    ExecuteInstruction(method, instruction, frameArgs, locals, stack, path, depth);
                    if (instruction.OpCode == OpCodes.Ret)
                        return stack.Count == 0 ? Value.Null() : stack[^1];
                }
                return stack.Count == 0 ? Value.Null() : stack[^1];
            }
            finally
            {
                _activeCalls.Remove(callKey);
            }
        }

        private void ExecuteInstruction(
            MethodDef method,
            PcCompatIlInstruction instruction,
            IReadOnlyList<Value> args,
            Value[] locals,
            List<Value> stack,
            string path,
            int depth)
        {
            var op = instruction.OpCode;
            if (op == OpCodes.Nop || op == OpCodes.Readonly || op == OpCodes.Volatile ||
                op == OpCodes.Tailcall || op == OpCodes.Constrained)
                return;

            if (TryLoadArgument(op, instruction, args, out var argument))
            {
                stack.Add(argument);
                return;
            }
            if (TryLoadLocal(op, instruction, locals, path, out var local))
            {
                stack.Add(local);
                return;
            }
            if (TryStoreLocal(op, instruction, locals, stack))
                return;
            if (TryLoadLocalAddress(op, instruction, path, stack))
                return;
            if (TryLoadConstant(method.Context.Reader, op, instruction, out var constant))
            {
                stack.Add(constant);
                return;
            }

            switch (op.Name)
            {
                case "ldnull":
                    stack.Add(Value.Null());
                    return;
                case "dup":
                    RequireStack(stack, method, instruction);
                    stack.Add(stack[^1]);
                    return;
                case "pop":
                    RequireStack(stack, method, instruction);
                    stack.RemoveAt(stack.Count - 1);
                    return;
                case "ldsfld":
                {
                    var field = PcCompatMetadataNames.GetFieldIdentity(method.Context.Reader, instruction.MetadataToken);
                    var fieldType = PcCompatMetadataNames.GetFieldType(method.Context.Reader, instruction.MetadataToken);
                    if (_graph.Fields.TryGetValue("static:" + field.DisplayName, out var value))
                        stack.Add(value);
                    else if (_resources.TryResolve(field, fieldType, out var resource))
                        stack.Add(Value.Resource(resource));
                    else
                        stack.Add(Value.Unknown(fieldType));
                    return;
                }
                case "stsfld":
                {
                    var value = Pop(stack, method, instruction);
                    var field = PcCompatMetadataNames.GetFieldIdentity(method.Context.Reader, instruction.MetadataToken);
                    _graph.Fields["static:" + field.DisplayName] = value;
                    return;
                }
                case "ldfld":
                {
                    var owner = Pop(stack, method, instruction);
                    var field = PcCompatMetadataNames.GetFieldIdentity(method.Context.Reader, instruction.MetadataToken);
                    var key = InstanceFieldKey(owner, field.DisplayName);
                    stack.Add(_graph.Fields.TryGetValue(key, out var value) ? value : Value.Unknown());
                    return;
                }
                case "stfld":
                {
                    var value = Pop(stack, method, instruction);
                    var owner = Pop(stack, method, instruction);
                    var field = PcCompatMetadataNames.GetFieldIdentity(method.Context.Reader, instruction.MetadataToken);
                    _graph.Fields[InstanceFieldKey(owner, field.DisplayName)] = value;
                    return;
                }
                case "ldflda":
                {
                    var owner = Pop(stack, method, instruction);
                    var field = PcCompatMetadataNames.GetFieldIdentity(method.Context.Reader, instruction.MetadataToken);
                    stack.Add(Value.Reference(InstanceFieldKey(owner, field.DisplayName)));
                    return;
                }
                case "ldind.ref":
                {
                    var reference = Pop(stack, method, instruction);
                    stack.Add(_graph.Fields.TryGetValue(reference.Key, out var value) ? value : Value.Unknown());
                    return;
                }
                case "stind.ref":
                {
                    var value = Pop(stack, method, instruction);
                    var reference = Pop(stack, method, instruction);
                    if (reference.Kind == ValueKind.Reference)
                        _graph.Fields[reference.Key] = value;
                    return;
                }
                case "newobj":
                    HandleNewObject(method, instruction, stack, path, depth);
                    return;
                case "call":
                case "callvirt":
                    HandleCall(method, instruction, stack, path, depth);
                    return;
                case "castclass":
                case "isinst":
                case "box":
                case "unbox.any":
                case "conv.i4":
                case "conv.i8":
                case "conv.r4":
                case "conv.r8":
                case "neg":
                    return;
                case "add":
                case "sub":
                case "mul":
                    ApplyNumericBinary(op.Name!, stack, method, instruction);
                    return;
                case "ldtoken":
                    stack.Add(Value.Unknown("System.RuntimeTypeHandle"));
                    return;
                case "ret":
                    return;
                default:
                    throw Abort("UnsupportedUiInstruction", $"Unsupported UI IL instruction {op.Name}.", method, instruction.Offset);
            }
        }

        private void HandleNewObject(
            MethodDef method,
            PcCompatIlInstruction instruction,
            List<Value> stack,
            string path,
            int depth)
        {
            var identity = PcCompatMetadataNames.GetMethodIdentity(method.Context.Reader, instruction.MetadataToken);
            var parameterTypes = PcCompatMetadataNames.GetMethodParameterTypes(method.Context.Reader, instruction.MetadataToken);
            var arguments = PopArguments(stack, parameterTypes.Count, method, instruction);
            var type = NormalizeTypeName(identity.DeclaringType);
            if (type == UnityGameObject)
            {
                if (arguments.Count > 1 || (arguments.Count == 1 && arguments[0].Kind != ValueKind.String))
                    throw Abort("DynamicGameObjectName", "GameObject name is not a compile-time string.", method, instruction.Offset);
                var name = arguments.Count == 0 ? string.Empty : arguments[0].Text;
                var key = method.DisplayName + "@" + instruction.Offset.ToString("X4") + "|" + name;
                var node = _graph.GetOrAddNode(key, name);
                stack.Add(Value.Node(key));
                return;
            }
            if (type is UnityVector2 or UnityVector3 or UnityColor)
            {
                stack.Add(BuildStruct(type, arguments));
                return;
            }

            var local = _index.Resolve(method.Context, instruction.MetadataToken);
            if (local != null && _relevant.Contains(local))
            {
                var objectKey = "object:" + local.DisplayName + "@" + method.DisplayName + "@" + instruction.Offset.ToString("X4");
                var instance = Value.Managed(objectKey, local.DeclaringType);
                Execute(local, instance, arguments, path + "/new:" + instruction.Offset.ToString("X4"), depth + 1);
                stack.Add(instance);
                return;
            }

            if (arguments.Any(value => value.IsUi))
                throw Abort("UnsupportedUiAllocation", $"UI helper allocation {identity.DisplayName} receives a Unity object.", method, instruction.Offset);
            stack.Add(Value.Unknown(type));
        }

        private void HandleCall(
            MethodDef method,
            PcCompatIlInstruction instruction,
            List<Value> stack,
            string path,
            int depth)
        {
            var reader = method.Context.Reader;
            var identity = PcCompatMetadataNames.GetMethodIdentity(reader, instruction.MetadataToken);
            var parameterTypes = PcCompatMetadataNames.GetMethodParameterTypes(reader, instruction.MetadataToken);
            var arguments = PopArguments(stack, parameterTypes.Count, method, instruction);
            var isInstance = _index.Resolve(method.Context, instruction.MetadataToken)?.IsStatic == false ||
                             PcCompatMetadataNames.GetMethodIsInstance(reader, instruction.MetadataToken) == true;
            var receiver = isInstance ? Pop(stack, method, instruction) : Value.Null();
            var type = NormalizeTypeName(identity.DeclaringType);
            var name = identity.Name;

            if (TryHandleValueTypeConstructor(type, name, receiver, arguments))
                return;
            if (TryHandleUnityConstant(type, name, receiver, arguments, out var unityConstant))
            {
                stack.Add(unityConstant);
                return;
            }
            if (TryHandleUiCall(type, name, receiver, arguments, instruction, method, stack))
                return;

            var local = _index.Resolve(method.Context, instruction.MetadataToken);
            if (local != null && _relevant.Contains(local))
            {
                var checkpoint = _graph.Capture();
                Value result;
                try
                {
                    result = Execute(
                        local,
                        local.IsStatic ? Value.Null() : receiver,
                        arguments,
                        path + "/call:" + instruction.Offset.ToString("X4"),
                        depth + 1);
                }
                catch (LoweringAbortException ex)
                {
                    _graph.Restore(checkpoint);
                    _issues.Add(new PcCompatUiLoweringIssue
                    {
                        Code = ex.Code,
                        Message = ex.Message,
                        Method = local.DisplayName,
                        IlOffset = ex.Offset
                    });
                    result = Value.Unknown(local.ReturnType);
                }
                if (!string.Equals(local.ReturnType, "System.Void", StringComparison.Ordinal))
                    stack.Add(result);
                return;
            }

            if (receiver.IsUi || arguments.Any(value => value.IsUi))
            {
                // Calls that only inspect a UI object are not a graph
                // operation. Calls that mutate one must be in the explicit
                // catalog above; rejecting here prevents silent drift.
                if (name.StartsWith("get_", StringComparison.Ordinal) &&
                    name is not "get_transform" and not "get_gameObject" and not "get_rectTransform")
                    throw Abort("UnsupportedUiGetter", $"Unsupported UI getter {identity.DisplayName}.", method, instruction.Offset);
                if (name.StartsWith("set_", StringComparison.Ordinal) ||
                    name is "Instantiate" or "Find" or "FindObjectOfType")
                    throw Abort("UnsupportedUiOperation", $"Unsupported UI operation {identity.DisplayName}.", method, instruction.Offset);
            }

            var returnType = PcCompatMetadataNames.GetMethodReturnType(reader, instruction.MetadataToken);
            if (!string.Equals(returnType, "System.Void", StringComparison.Ordinal))
                stack.Add(Value.Unknown(returnType));
        }

        private bool TryHandleValueTypeConstructor(
            string type,
            string name,
            Value receiver,
            IReadOnlyList<Value> arguments)
        {
            if (name != ".ctor" || receiver.Kind != ValueKind.Reference)
                return false;
            var value = BuildStruct(type, arguments);
            _graph.Fields[receiver.Key] = value;
            return type is UnityVector2 or UnityVector3 or UnityColor;
        }

        private static bool TryHandleUnityConstant(
            string type,
            string name,
            Value receiver,
            IReadOnlyList<Value> arguments,
            out Value value)
        {
            value = Value.Unknown();
            if (receiver.Kind != ValueKind.Null || arguments.Count != 0)
                return false;

            value = (type, name) switch
            {
                (UnityVector2, "get_zero") => Value.Vector2(0, 0),
                (UnityVector2, "get_one") => Value.Vector2(1, 1),
                (UnityVector3, "get_zero") => Value.Vector3(0, 0, 0),
                (UnityVector3, "get_one") => Value.Vector3(1, 1, 1),
                (UnityColor, "get_black") => Value.Color(0, 0, 0, 1),
                (UnityColor, "get_white") => Value.Color(1, 1, 1, 1),
                (UnityColor, "get_clear") => Value.Color(0, 0, 0, 0),
                _ => Value.Unknown()
            };
            return value.Kind != ValueKind.Unknown;
        }

        private bool TryHandleUiCall(
            string type,
            string name,
            Value receiver,
            IReadOnlyList<Value> arguments,
            PcCompatIlInstruction instruction,
            MethodDef method,
            List<Value> stack)
        {
            if (name == "AddComponent" || name == "GetComponent")
            {
                if (receiver.Kind != ValueKind.Node)
                    throw Abort("UnknownUiReceiver", $"{name} receiver is not a known GameObject.", method, instruction.Offset);
                var generic = PcCompatMetadataNames.GetMethodGenericArguments(method.Context.Reader, instruction.MetadataToken);
                if (generic.Count != 1)
                    throw Abort("DynamicUiComponentType", $"{name} generic component type is not statically known.", method, instruction.Offset);
                var componentType = NormalizeTypeName(generic[0]);
                var mask = ComponentMask(componentType);
                if (mask == PcCompatUiComponentMask.None)
                    throw Abort("UnsupportedUiComponent", $"Component {componentType} is outside the native UI catalog.", method, instruction.Offset);
                var node = GetNode(receiver, method, instruction);
                node.Components |= mask;
                stack.Add(Value.Component(receiver.Key, componentType));
                return true;
            }

            if (name == "get_transform")
            {
                var nodeKey = ToNodeKey(receiver);
                if (nodeKey == null)
                    throw Abort("UnknownUiTransform", "Transform getter receiver is not a known UI object.", method, instruction.Offset);
                stack.Add(Value.Component(nodeKey, UnityTransform));
                return true;
            }
            if (name == "get_gameObject")
            {
                var nodeKey = ToNodeKey(receiver);
                if (nodeKey == null)
                    throw Abort("UnknownUiGameObject", "gameObject getter receiver is not a known UI object.", method, instruction.Offset);
                stack.Add(Value.Node(nodeKey));
                return true;
            }
            if (name == "get_rectTransform")
            {
                var nodeKey = ToNodeKey(receiver);
                if (nodeKey == null)
                    throw Abort("UnknownUiRectTransform", "rectTransform getter receiver is not a known UI object.", method, instruction.Offset);
                var node = GetNode(Value.Node(nodeKey), method, instruction);
                node.Components |= PcCompatUiComponentMask.RectTransform;
                stack.Add(Value.Component(nodeKey, UnityRectTransform));
                return true;
            }
            if (name == "SetParent")
            {
                var child = ToNodeKey(receiver);
                var parent = arguments.Count == 1 ? ToNodeKey(arguments[0]) : null;
                if (child == null || parent == null)
                    throw Abort("DynamicUiParent", "SetParent requires two statically known UI transforms.", method, instruction.Offset);
                var node = GetNode(Value.Node(child), method, instruction);
                if (node.ParentKey != null && node.ParentKey != parent)
                    throw Abort("ConflictingUiParent", $"UI node {node.Name} has multiple parent identities.", method, instruction.Offset);
                node.ParentKey = parent;
                return true;
            }
            if (name == "SetActive")
            {
                var node = GetNode(receiver, method, instruction);
                if (arguments.Count != 1 || !TryInteger(arguments[0], out var active))
                    throw Abort("DynamicUiActive", "SetActive requires a constant boolean.", method, instruction.Offset);
                node.ExplicitActive = true;
                node.Active = active != 0;
                AddOperation(node, new PcCompatUiComponentOperation
                {
                    OpCode = PcCompatUiComponentOpCode.SetActive,
                    Payload0 = active == 0 ? 0 : 1
                });
                return true;
            }
            if (name == "DontDestroyOnLoad")
            {
                var node = GetNode(arguments.FirstOrDefault() ?? Value.Unknown(), method, instruction);
                node.DontDestroy = true;
                return true;
            }

            if (name.StartsWith("set_", StringComparison.Ordinal))
            {
                var node = GetNode(receiver, method, instruction);
                var property = name[4..];
                if (!TryAddPropertyOperation(node, type, property, arguments, method, instruction))
                    throw Abort("UnsupportedUiProperty", $"Unsupported UI property {type}.{property}.", method, instruction.Offset);
                return true;
            }

            return false;
        }

        private bool TryAddPropertyOperation(
            NodeDraft node,
            string type,
            string property,
            IReadOnlyList<Value> arguments,
            MethodDef method,
            PcCompatIlInstruction instruction)
        {
            if (arguments.Count != 1)
                return false;
            var value = arguments[0];
            if (value.Kind == ValueKind.Resource && value.ResourceBinding != null)
            {
                if (!TryMapResourceTarget(type, property, value.ResourceBinding.ExpectedType, out var target))
                    throw Abort(
                        "UiResourceTargetMismatch",
                        $"Verified resource {value.ResourceBinding.AssetName} ({value.ResourceBinding.ExpectedType}) cannot initialize {type}.{property}.",
                        method,
                        instruction.Offset);
                AddResourceBinding(node, target, value.ResourceBinding, method, instruction);
                return true;
            }
            PcCompatUiComponentOperation? operation = null;
            if (type == UnityRectTransform)
            {
                if (property is "anchorMin" or "anchorMax" or "anchoredPosition" or "sizeDelta")
                {
                    if (value.Kind != ValueKind.Vector2)
                        return false;
                    var pair = (value.X, value.Y);
                    switch (property)
                    {
                        case "anchorMin":
                            node.AnchorMin = pair;
                            return true;
                        case "anchorMax":
                            node.AnchorMax = pair;
                            return true;
                        case "anchoredPosition":
                            node.AnchoredPosition = pair;
                            return true;
                        case "sizeDelta":
                            node.SizeDelta = pair;
                            return true;
                    }
                }
                operation = property switch
                {
                    "pivot" => Vector2Operation(PcCompatUiComponentOpCode.SetPivot, value, true, node, method, instruction),
                    "localScale" => Vector3Operation(value, node, method, instruction),
                    _ => null
                };
            }
            else if (type == UnityCanvas)
            {
                operation = property switch
                {
                    "renderMode" when TryInteger(value, out var renderMode) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetCanvasRenderMode,
                        Payload0 = renderMode
                    },
                    "sortingOrder" when TryInteger(value, out var sortingOrder) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetCanvasSortingOrder,
                        Payload0 = sortingOrder
                    },
                    _ => null
                };
            }
            else if (type == UnityCanvasScaler)
            {
                operation = property switch
                {
                    "uiScaleMode" when TryInteger(value, out var scaleMode) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetCanvasScaleMode,
                        Payload0 = scaleMode
                    },
                    "referenceResolution" => Vector2Operation(PcCompatUiComponentOpCode.SetCanvasReferenceResolution, value, true, node, method, instruction),
                    "matchWidthOrHeight" when TryFloat(value, out var match) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetCanvasMatch,
                        Payload0 = FloatBits(match)
                    },
                    _ => null
                };
            }
            else if (type == UnityContentSizeFitter)
            {
                operation = property switch
                {
                    "horizontalFit" when TryInteger(value, out var horizontalFit) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetContentSizeHorizontalFit,
                        Payload0 = horizontalFit
                    },
                    "verticalFit" when TryInteger(value, out var verticalFit) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetContentSizeVerticalFit,
                        Payload0 = verticalFit
                    },
                    _ => null
                };
            }
            else if (type is UnityImage or UnityRawImage or UnityGraphic or TmpText or TmpTextBase)
            {
                if (property == "font" &&
                    string.Equals(value.TypeName, "TMPro.TMP_FontAsset", StringComparison.Ordinal))
                {
                    _issues.Add(new PcCompatUiLoweringIssue
                    {
                        Code = "ResourceFallback",
                        Message = "TMP font asset reference was omitted; native sink will use the platform default font.",
                        Method = method.DisplayName,
                        IlOffset = instruction.Offset,
                        Severity = "warning"
                    });
                    return true;
                }
                if (property is "font" or "fontSharedMaterial" or "fontMaterial")
                    throw Abort(
                        "DynamicUiResource",
                        "Font/material assignment requires a verified resource mapping.",
                        method,
                        instruction.Offset);
                operation = property switch
                {
                    "color" => ColorOperation(value, method, instruction),
                    "raycastTarget" when TryInteger(value, out var raycast) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetGraphicRaycastTarget,
                        Payload0 = raycast == 0 ? 0 : 1
                    },
                    "text" when value.Kind == ValueKind.String => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetText,
                        StringValue = value.Text
                    },
                    "fontSize" when TryFloat(value, out var fontSize) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetTextFontSize,
                        Payload0 = FloatBits(fontSize)
                    },
                    "alignment" when TryInteger(value, out var alignment) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetTextAlignment,
                        Payload0 = alignment
                    },
                    "supportRichText" when TryInteger(value, out var richText) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetTextRichText,
                        Payload0 = richText == 0 ? 0 : 1
                    },
                    "lineSpacing" when TryFloat(value, out var lineSpacing) => new PcCompatUiComponentOperation
                    {
                        OpCode = PcCompatUiComponentOpCode.SetTextLineSpacing,
                        Payload0 = FloatBits(lineSpacing)
                    },
                    _ => null
                };
            }

            if (operation == null)
                return false;
            AddOperation(node, operation);
            return true;
        }

        private static PcCompatUiComponentOperation? Vector2Operation(
            PcCompatUiComponentOpCode op,
            Value value,
            bool first,
            NodeDraft node,
            MethodDef method,
            PcCompatIlInstruction instruction)
        {
            if (value.Kind != ValueKind.Vector2)
                return null;
            if (op == PcCompatUiComponentOpCode.SetRect)
            {
                // The two RectTransform properties share the native SetRect
                // record. A later operation may fill the other pair; using
                // zero for the unused pair is intentionally conservative.
                return first
                    ? new PcCompatUiComponentOperation
                    {
                        OpCode = op,
                        Payload0 = FloatBits(value.X),
                        Payload1 = FloatBits(value.Y),
                        Payload2 = 0,
                        Payload3 = 0
                    }
                    : new PcCompatUiComponentOperation
                    {
                        OpCode = op,
                        Payload0 = 0,
                        Payload1 = 0,
                        Payload2 = FloatBits(value.X),
                        Payload3 = FloatBits(value.Y)
                    };
            }
            return op switch
            {
                PcCompatUiComponentOpCode.SetAnchors => first
                    ? new PcCompatUiComponentOperation
                    {
                        OpCode = op,
                        Payload0 = FloatBits(value.X),
                        Payload1 = FloatBits(value.Y),
                        Payload2 = FloatBits(value.X),
                        Payload3 = FloatBits(value.Y)
                    }
                    : null,
                PcCompatUiComponentOpCode.SetPivot when first => new PcCompatUiComponentOperation
                {
                    OpCode = op,
                    Payload0 = FloatBits(value.X),
                    Payload1 = FloatBits(value.Y)
                },
                PcCompatUiComponentOpCode.SetCanvasReferenceResolution when first => new PcCompatUiComponentOperation
                {
                    OpCode = op,
                    Payload0 = FloatBits(value.X),
                    Payload1 = FloatBits(value.Y)
                },
                _ => null
            };
        }

        private static PcCompatUiComponentOperation? Vector3Operation(
            Value value,
            NodeDraft node,
            MethodDef method,
            PcCompatIlInstruction instruction)
            => value.Kind == ValueKind.Vector3
                ? new PcCompatUiComponentOperation
                {
                    OpCode = PcCompatUiComponentOpCode.SetLocalScale,
                    Payload0 = FloatBits(value.X),
                    Payload1 = FloatBits(value.Y),
                    Payload2 = FloatBits(value.Z)
                }
                : null;

        private static PcCompatUiComponentOperation? ColorOperation(
            Value value,
            MethodDef method,
            PcCompatIlInstruction instruction)
            => value.Kind == ValueKind.Color
                ? new PcCompatUiComponentOperation
                {
                    OpCode = PcCompatUiComponentOpCode.SetGraphicColor,
                    Payload0 = FloatBits(value.X),
                    Payload1 = FloatBits(value.Y),
                    Payload2 = FloatBits(value.Z),
                    Payload3 = FloatBits(value.W)
                }
                : null;

        private static PcCompatUiComponentMask ComponentMask(string type)
            => type switch
            {
                UnityRectTransform => PcCompatUiComponentMask.RectTransform,
                UnityCanvas => PcCompatUiComponentMask.Canvas,
                UnityCanvasScaler => PcCompatUiComponentMask.CanvasScaler,
                UnityContentSizeFitter => PcCompatUiComponentMask.ContentSizeFitter,
                UnityImage => PcCompatUiComponentMask.Image,
                UnityRawImage => PcCompatUiComponentMask.RawImage,
                TmpText => PcCompatUiComponentMask.TextMeshProUGUI,
                UnityCanvasRenderer => PcCompatUiComponentMask.CanvasRenderer,
                _ => PcCompatUiComponentMask.None
            };

        private static bool TryMapResourceTarget(
            string componentType,
            string property,
            string expectedType,
            out PcCompatUiResourceTarget target)
        {
            target = default;
            if (componentType == UnityImage && property == "sprite" &&
                ResourceTypeIs(expectedType, "Sprite"))
            {
                target = PcCompatUiResourceTarget.ImageSprite;
                return true;
            }
            if (componentType == UnityRawImage && property == "texture" &&
                (ResourceTypeIs(expectedType, "Texture2D") || ResourceTypeIs(expectedType, "Texture")))
            {
                target = PcCompatUiResourceTarget.RawImageTexture;
                return true;
            }
            if (componentType is UnityImage or UnityRawImage or UnityGraphic or TmpText or TmpTextBase &&
                property == "material" && ResourceTypeIs(expectedType, "Material"))
            {
                target = PcCompatUiResourceTarget.GraphicMaterial;
                return true;
            }
            if (componentType is TmpText or TmpTextBase && property == "font" &&
                ResourceTypeIs(expectedType, "TMP_FontAsset"))
            {
                target = PcCompatUiResourceTarget.TextFont;
                return true;
            }
            if (componentType is TmpText or TmpTextBase && property == "fontSharedMaterial" &&
                ResourceTypeIs(expectedType, "Material"))
            {
                target = PcCompatUiResourceTarget.TextFontSharedMaterial;
                return true;
            }
            if (componentType is TmpText or TmpTextBase && property == "fontMaterial" &&
                ResourceTypeIs(expectedType, "Material"))
            {
                target = PcCompatUiResourceTarget.TextFontMaterial;
                return true;
            }
            return false;
        }

        private static bool ResourceTypeIs(string actual, string expected)
        {
            var normalized = actual.Split(',', 2)[0].Trim();
            var simple = normalized[(normalized.LastIndexOf('.') + 1)..];
            return simple.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddResourceBinding(
            NodeDraft node,
            PcCompatUiResourceTarget target,
            PcCompatResourceBinding binding,
            MethodDef method,
            PcCompatIlInstruction instruction)
        {
            var existing = node.Resources.FirstOrDefault(candidate => candidate.Target == target);
            if (existing == null)
            {
                node.Resources.Add(new ResourceBindingDraft
                {
                    Target = target,
                    Binding = binding
                });
                return;
            }
            if (existing.Binding.FeatureGroupId.Equals(binding.FeatureGroupId, StringComparison.OrdinalIgnoreCase) &&
                existing.Binding.AssetName.Equals(binding.AssetName, StringComparison.Ordinal) &&
                existing.Binding.ExpectedType.Equals(binding.ExpectedType, StringComparison.OrdinalIgnoreCase))
                return;
            throw Abort(
                "ConflictingUiResource",
                $"UI node {node.Name} assigns multiple resources to {target}.",
                method,
                instruction.Offset);
        }

        private NodeDraft GetNode(Value value, MethodDef method, PcCompatIlInstruction instruction)
        {
            var key = ToNodeKey(value);
            if (key == null || !_graph.TryGetNode(key, out var node))
                throw Abort("UnknownUiObject", "UI operation targets an object outside the lowered graph.", method, instruction.Offset);
            return node;
        }

        private static string? ToNodeKey(Value value)
            => value.Kind is ValueKind.Node or ValueKind.Component ? value.Key : null;

        private static string InstanceFieldKey(Value owner, string field)
            => (owner.Kind == ValueKind.Managed ? "instance:" + owner.Key : "unknown-instance") + ":" + field;

        private static void AddOperation(NodeDraft node, PcCompatUiComponentOperation operation)
        {
            if (!node.Operations.Any(existing =>
                    existing.OpCode == operation.OpCode &&
                    existing.StringValue == operation.StringValue &&
                    existing.Payload0 == operation.Payload0 &&
                    existing.Payload1 == operation.Payload1 &&
                    existing.Payload2 == operation.Payload2 &&
                    existing.Payload3 == operation.Payload3))
                node.Operations.Add(operation);
        }

        private static Value BuildStruct(string type, IReadOnlyList<Value> args)
        {
            var values = args.Select(value => TryFloat(value, out var number) ? number : 0.0).ToArray();
            return type switch
            {
                UnityVector2 => Value.Vector2(values.ElementAtOrDefault(0), values.ElementAtOrDefault(1)),
                UnityVector3 => Value.Vector3(values.ElementAtOrDefault(0), values.ElementAtOrDefault(1), values.ElementAtOrDefault(2)),
                UnityColor => Value.Color(values.ElementAtOrDefault(0), values.ElementAtOrDefault(1), values.ElementAtOrDefault(2), values.Length > 3 ? values[3] : 1.0),
                _ => Value.Unknown(type)
            };
        }

        private static bool TryLoadConstant(
            MetadataReader reader,
            OpCode op,
            PcCompatIlInstruction instruction,
            out Value value)
        {
            value = Value.Unknown();
            if (op == OpCodes.Ldstr)
            {
                value = Value.String(PcCompatMetadataNames.GetUserString(reader, instruction.MetadataToken));
                return true;
            }
            if (op == OpCodes.Ldc_I4_M1)
            {
                value = Value.Int(-1);
                return true;
            }
            if (op.Name is not null && op.Name.StartsWith("ldc.i4.", StringComparison.Ordinal) &&
                int.TryParse(op.Name[7..], out var shortValue))
            {
                value = Value.Int(shortValue);
                return true;
            }
            if (op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4)
            {
                value = Value.Int(Convert.ToInt32(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            if (op == OpCodes.Ldc_I8)
            {
                value = Value.Int(Convert.ToInt64(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture), "System.Int64");
                return true;
            }
            if (op == OpCodes.Ldc_R4 || op == OpCodes.Ldc_R8)
            {
                value = Value.Float(Convert.ToDouble(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            return false;
        }

        private static bool TryLoadArgument(OpCode op, PcCompatIlInstruction instruction, IReadOnlyList<Value> args, out Value value)
        {
            value = Value.Unknown();
            var index = op.Name switch
            {
                "ldarg.0" => 0,
                "ldarg.1" => 1,
                "ldarg.2" => 2,
                "ldarg.3" => 3,
                "ldarg.s" or "ldarg" => instruction.Operand is int i ? i : -1,
                _ => -1
            };
            if (index < 0 || index >= args.Count)
                return false;
            value = args[index];
            return true;
        }

        private bool TryLoadLocal(
            OpCode op,
            PcCompatIlInstruction instruction,
            Value[] locals,
            string path,
            out Value value)
        {
            value = Value.Unknown();
            var index = LocalIndex(op, instruction);
            if (index < 0 || index >= locals.Length || op.Name?.StartsWith("stloc", StringComparison.Ordinal) == true)
                return false;
            value = locals[index];
            if (value.Kind == ValueKind.Unknown &&
                _graph.Fields.TryGetValue("local:" + path + ":" + index, out var constructed))
                value = constructed;
            return true;
        }

        private static bool TryStoreLocal(OpCode op, PcCompatIlInstruction instruction, Value[] locals, List<Value> stack)
        {
            var index = LocalIndex(op, instruction);
            if (index < 0 || index >= locals.Length || op.Name?.StartsWith("stloc", StringComparison.Ordinal) != true)
                return false;
            locals[index] = stack.Count == 0 ? Value.Unknown() : stack[^1];
            if (stack.Count != 0)
                stack.RemoveAt(stack.Count - 1);
            return true;
        }

        private static bool TryLoadLocalAddress(
            OpCode op,
            PcCompatIlInstruction instruction,
            string path,
            List<Value> stack)
        {
            var index = op.Name switch
            {
                "ldloca.s" or "ldloca" => instruction.Operand is int i ? i : -1,
                "ldarga.s" or "ldarga" => instruction.Operand is int i ? i : -1,
                _ => -1
            };
            if (index < 0)
                return false;
            stack.Add(Value.Reference("local:" + path + ":" + index));
            return true;
        }

        private static int LocalIndex(OpCode op, PcCompatIlInstruction instruction)
        {
            if (op.Name is null)
                return -1;
            if (op.Name is "ldloc.0" or "stloc.0") return 0;
            if (op.Name is "ldloc.1" or "stloc.1") return 1;
            if (op.Name is "ldloc.2" or "stloc.2") return 2;
            if (op.Name is "ldloc.3" or "stloc.3") return 3;
            if (op.Name is "ldloc.s" or "stloc.s" or "ldloc" or "stloc")
                return instruction.Operand is int i ? i : -1;
            return -1;
        }

        private static IReadOnlyList<Value> PopArguments(
            List<Value> stack,
            int count,
            MethodDef method,
            PcCompatIlInstruction instruction)
        {
            if (stack.Count < count)
                throw Abort("StackUnderflow", "IL evaluation stack underflow while reading call arguments.", method, instruction.Offset);
            var values = new Value[count];
            for (var index = count - 1; index >= 0; --index)
            {
                values[index] = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
            return values;
        }

        private static Value Pop(List<Value> stack, MethodDef method, PcCompatIlInstruction instruction)
        {
            if (stack.Count == 0)
                throw Abort("StackUnderflow", "IL evaluation stack underflow.", method, instruction.Offset);
            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        private static void RequireStack(List<Value> stack, MethodDef method, PcCompatIlInstruction instruction)
        {
            if (stack.Count == 0)
                throw Abort("StackUnderflow", "IL evaluation stack underflow.", method, instruction.Offset);
        }

        private static bool TryInteger(Value value, out long result)
        {
            if (value.Kind == ValueKind.Integer)
            {
                result = value.Integer;
                return true;
            }
            if (value.Kind == ValueKind.Float && double.IsInteger(value.Number))
            {
                result = (long)value.Number;
                return true;
            }
            result = 0;
            return false;
        }

        private static bool TryFloat(Value value, out double result)
        {
            if (value.Kind == ValueKind.Float)
            {
                result = value.Number;
                return true;
            }
            if (value.Kind == ValueKind.Integer)
            {
                result = value.Integer;
                return true;
            }
            result = 0;
            return false;
        }

        private static long FloatBits(double value)
            => BitConverter.SingleToInt32Bits((float)value);

        private static void ApplyNumericBinary(string name, List<Value> stack, MethodDef method, PcCompatIlInstruction instruction)
        {
            var right = Pop(stack, method, instruction);
            var left = Pop(stack, method, instruction);
            if (!TryFloat(left, out var lhs) || !TryFloat(right, out var rhs))
            {
                stack.Add(Value.Unknown());
                return;
            }
            stack.Add(Value.Float(name switch
            {
                "add" => lhs + rhs,
                "sub" => lhs - rhs,
                "mul" => lhs * rhs,
                _ => 0
            }));
        }

        private static bool HasUiSeedAfter(
            IReadOnlyList<PcCompatIlInstruction> instructions,
            int offset,
            MetadataReader reader)
            => instructions.Any(instruction =>
                instruction.Offset > offset && IsDirectUiSeed(reader, instruction));

        private static LoweringAbortException Abort(string code, string message, MethodDef method, int? offset)
            => new(code, message, offset);
    }

    private sealed class LoweringAbortException : Exception
    {
        public LoweringAbortException(string code, string message, int? offset)
            : base(message)
        {
            Code = code;
            Offset = offset;
        }

        public string Code { get; }
        public int? Offset { get; }
    }

    private sealed class AssemblyIndex : IDisposable
    {
        private readonly List<AssemblyContext> _contexts = new();
        private readonly Dictionary<string, List<MethodDef>> _methods = new(StringComparer.Ordinal);

        public AssemblyIndex(IEnumerable<string> paths, List<PcCompatUiLoweringIssue> issues)
        {
            foreach (var path in paths)
            {
                try
                {
                    var context = new AssemblyContext(path);
                    _contexts.Add(context);
                    foreach (var method in context.Methods)
                    {
                        var key = MethodKey(method.DeclaringType, method.Name, method.ParameterTypes);
                        if (!_methods.TryGetValue(key, out var list))
                        {
                            list = new List<MethodDef>();
                            _methods.Add(key, list);
                        }
                        list.Add(method);
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(new PcCompatUiLoweringIssue
                    {
                        Code = "AssemblyIndexFailed",
                        Message = $"{path}: {ex.GetType().Name}: {ex.Message}",
                        Severity = "info"
                    });
                }
            }
        }

        public IReadOnlyList<MethodDef> Find(string type, string method)
            => _methods.Values
                .SelectMany(values => values)
                .Where(candidate => candidate.DeclaringType == type && candidate.Name == method)
                .ToArray();

        public MethodDef? Resolve(AssemblyContext context, int token)
        {
            var identity = PcCompatMetadataNames.GetMethodIdentity(context.Reader, token);
            if (identity.IsEmpty)
                return null;
            var parameters = PcCompatMetadataNames.GetMethodParameterTypes(context.Reader, token);
            var key = MethodKey(identity.DeclaringType, identity.Name, parameters);
            if (!_methods.TryGetValue(key, out var methods))
                return null;
            var sameAssembly = methods.Where(method => ReferenceEquals(method.Context, context)).ToArray();
            return sameAssembly.Length == 1 ? sameAssembly[0] : null;
        }

        public static string MethodKey(string type, string method, IReadOnlyList<string> parameters)
            => NormalizeTypeName(type) + "|" + method + "|" + string.Join(';', parameters);

        public void Dispose()
        {
            foreach (var context in _contexts)
                context.Dispose();
        }
    }

    private sealed class AssemblyContext : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _peReader;

        public AssemblyContext(string path)
        {
            _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _peReader = new PEReader(_stream, PEStreamOptions.LeaveOpen);
            Reader = _peReader.GetMetadataReader();
            Methods = BuildMethods();
        }

        public MetadataReader Reader { get; }
        public IReadOnlyList<MethodDef> Methods { get; }

        private IReadOnlyList<MethodDef> BuildMethods()
        {
            var result = new List<MethodDef>();
            foreach (var typeHandle in Reader.TypeDefinitions)
            {
                var type = Reader.GetTypeDefinition(typeHandle);
                var typeName = PcCompatMetadataNames.GetTypeFullName(Reader, typeHandle);
                foreach (var handle in type.GetMethods())
                {
                    var method = Reader.GetMethodDefinition(handle);
                    var parameterTypes = PcCompatMetadataNames.GetMethodParameterTypes(Reader, handle);
                    var token = MetadataTokens.GetToken(handle);
                    MethodBodyBlock? body = null;
                    if (method.RelativeVirtualAddress != 0)
                    {
                        try
                        {
                            body = _peReader.GetMethodBody(method.RelativeVirtualAddress);
                        }
                        catch
                        {
                            body = null;
                        }
                    }
                    result.Add(new MethodDef
                    {
                        Context = this,
                        Token = token,
                        DeclaringType = NormalizeTypeName(typeName),
                        Name = Reader.GetString(method.Name),
                        ParameterTypes = parameterTypes,
                        ReturnType = PcCompatMetadataNames.GetMethodReturnType(Reader, token),
                        IsStatic = (method.Attributes & MethodAttributes.Static) != 0,
                        Body = body
                    });
                }
            }
            return result;
        }

        public void Dispose()
        {
            _peReader.Dispose();
            _stream.Dispose();
        }
    }

    private sealed class MethodDef
    {
        public required AssemblyContext Context { get; init; }
        public required int Token { get; init; }
        public required string DeclaringType { get; init; }
        public required string Name { get; init; }
        public required IReadOnlyList<string> ParameterTypes { get; init; }
        public required string ReturnType { get; init; }
        public required bool IsStatic { get; init; }
        public required MethodBodyBlock? Body { get; init; }
        public string Identity => DeclaringType + "." + Name + "(" + string.Join(',', ParameterTypes) + ")";
        public string DisplayName => Identity;
    }
}
