using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.IO;
using Xphorror.PcModCompat.Tools;

var options = CommandLineOptions.Parse(args);
if (options is null)
    return 2;

try
{
    var result = await new ClosureBuilder(options).BuildAsync();
    await result.WriteAsync(options);
    Console.WriteLine(
        $"Closure selected {result.SelectedTypes.Count} exact types in " +
        $"{result.SelectedTypes.Select(type => type.AssemblyName).Distinct(StringComparer.OrdinalIgnoreCase).Count()} assemblies; " +
        $"missingAndroid={result.MissingAndroidTypes.Count}, unresolvedMetadata={result.UnresolvedMetadataTypes.Count}");
    return result.MissingAndroidTypes.Count == 0 && result.UnresolvedMetadataTypes.Count == 0 ? 0 : 4;
}
catch (DecoderFallbackException exception)
{
    Console.Error.WriteLine($"Input is not valid UTF-8: {exception.Message}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

internal sealed class ClosureBuilder
{
    private static readonly HashSet<string> CorlibAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib",
        "System.Private.CoreLib"
    };

    private readonly CommandLineOptions _options;
    private readonly Dictionary<TypeKey, List<AndroidTypeRecord>> _androidTypes = new();
    private readonly Dictionary<string, List<AndroidTypeRecord>> _androidTypesByName =
        new(StringComparer.Ordinal);
    private readonly Dictionary<TypeKey, TypeDefinition> _pcTypes = new();
    private readonly Dictionary<TypeDefinition, string> _assemblyPaths = new();
    private readonly Dictionary<TypeKey, ClosureNode> _selected = new();
    private readonly Queue<ClosureNode> _pending = new();
    private readonly HashSet<TypeKey> _scanned = new();
    private readonly List<MissingTypeRecord> _missingAndroid = new();
    private readonly List<UnresolvedTypeRecord> _unresolvedMetadata = new();
    private readonly List<UnresolvedReflectedMemberRecord> _unresolvedReflectedMembers = new();
    private readonly HashSet<string> _missingDedup = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvedDedup = new(StringComparer.Ordinal);

    public ClosureBuilder(CommandLineOptions options) => _options = options;

    public async Task<ClosureResult> BuildAsync()
    {
        await LoadAndroidCatalogAsync();
        LoadPcAssemblies();
        AddRequiredCorlibType("System.Object");
        AddRequiredCorlibType("System.Attribute");
        AddRequiredCorlibType("System.ValueType");
        AddRequiredCorlibType("System.Enum");
        AddRequiredCorlibType("System.Nullable`1");
        AddRequiredCorlibType("System.Type");
        AddRequiredCorlibType("System.RuntimeTypeHandle");
        AddRequiredCorlibType("System.Delegate");
        AddRequiredCorlibType("System.MulticastDelegate");
        AddRequiredCorlibType("System.Reflection.MethodInfo");

        var seeds = ReadStrictUtf8Lines(_options.SeedPath).ToArray();
        if (seeds.Length == 0)
            throw new InvalidOperationException("The proxy seed file is empty.");

        foreach (var seed in seeds)
        {
            var androidType = ResolveSeed(seed);
            var key = TypeKey.Create(androidType.AssemblyName, androidType.FullName);
            if (!_pcTypes.TryGetValue(key, out var pcType))
                throw new InvalidOperationException(
                    $"Seed exists in Android metadata but not in PC assemblies: {key}");
            AddType(pcType, null, $"seed:{seed}");
        }

        ApplySurfaceManifest();

        while (_pending.TryDequeue(out var node))
        {
            if (!_scanned.Add(node.Key))
                continue;
            ScanNode(node);
        }

        var selected = _selected.Values
            .Select(node => new SelectedTypeRecord(
                node.Key.AssemblyName,
                node.Key.FullName,
                node.Mode.ToString().ToLowerInvariant(),
                node.Fields.Count,
                node.Methods.Count,
                node.Properties.Count,
                _assemblyPaths[node.Type],
                BuildPath(node.Key)))
            .OrderBy(record => record.AssemblyName, StringComparer.Ordinal)
            .ThenBy(record => record.FullName, StringComparer.Ordinal)
            .ToArray();

        return new ClosureResult(
            _options.AndroidCatalogPath,
            _options.AssemblyDirectory,
            _options.SeedPath,
            _options.SurfacePath,
            seeds,
            selected,
            _selected.Values.OrderBy(node => node.Key.AssemblyName, StringComparer.Ordinal)
                .ThenBy(node => node.Key.FullName, StringComparer.Ordinal).ToArray(),
            _missingAndroid.OrderBy(record => record.AssemblyName, StringComparer.Ordinal)
                .ThenBy(record => record.FullName, StringComparer.Ordinal).ToArray(),
            _unresolvedMetadata.OrderBy(record => record.TypeName, StringComparer.Ordinal).ToArray(),
            _unresolvedReflectedMembers.OrderBy(record => record.Entry, StringComparer.Ordinal).ToArray());
    }

    private async Task LoadAndroidCatalogAsync()
    {
        await using var stream = File.OpenRead(_options.AndroidCatalogPath);
        var catalog = await JsonSerializer.DeserializeAsync<AndroidTypeCatalog>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (catalog is null || catalog.FormatVersion != "xphorror.android-il2cpp-type-catalog.v1")
            throw new InvalidOperationException("Unsupported or invalid Android type catalog.");

        foreach (var type in catalog.Types)
        {
            var key = TypeKey.Create(type.AssemblyName, type.FullName);
            if (!_androidTypes.TryGetValue(key, out var sameIdentity))
                _androidTypes[key] = sameIdentity = new List<AndroidTypeRecord>();
            sameIdentity.Add(type);

            if (!_androidTypesByName.TryGetValue(type.Name, out var sameName))
                _androidTypesByName[type.Name] = sameName = new List<AndroidTypeRecord>();
            sameName.Add(type);
        }
    }

    private void LoadPcAssemblies()
    {
        var paths = Directory.EnumerateFiles(_options.AssemblyDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
            throw new InvalidOperationException($"No PC assemblies found: {_options.AssemblyDirectory}");

        var resolver = new ClosureAssemblyResolver();
        var assemblies = new List<(string Path, AssemblyDefinition Assembly)>();
        foreach (var path in paths)
        {
            var assembly = AssemblyDefinition.FromFile(path);
            assemblies.Add((path, assembly));
            resolver.AddToCache(assembly);
        }

        foreach (var (path, assembly) in assemblies)
        {
            assembly.ManifestModule!.MetadataResolver = new DefaultMetadataResolver(resolver);
            foreach (var type in assembly.ManifestModule.TopLevelTypes)
                IndexTypeTree(type, path);
        }
    }

    private void IndexTypeTree(TypeDefinition type, string path)
    {
        var key = GetTypeKey(type);
        if (!_pcTypes.TryAdd(key, type))
            throw new InvalidOperationException($"Duplicate PC type identity: {key}");
        _assemblyPaths[type] = path;
        foreach (var nested in type.NestedTypes)
            IndexTypeTree(nested, path);
    }

    private void ApplySurfaceManifest()
    {
        foreach (var entry in ReadStrictUtf8Lines(_options.SurfacePath))
        {
            // Bridge-owned calls are rewritten to host implementations and must not enter a
            // shared proxy static constructor. Keeping one here makes an unrelated MOD fail as
            // soon as that proxy type initializes, even if it never calls the owned member.
            if (ManagedBridgeOwnedSurface.Contains(entry))
                continue;

            var parts = entry.Split('|');
            if (parts.Length < 3)
                throw new InvalidDataException($"Invalid proxy surface entry: {entry}");

            var typeKey = TypeKey.Create(parts[1], parts[2]);
            if (!_pcTypes.TryGetValue(typeKey, out var type))
                throw new InvalidDataException($"Proxy surface type is absent from PC assemblies: {typeKey}");
            if (!HasAndroidType(typeKey, type))
                throw new InvalidDataException($"Proxy surface type is absent from Android metadata: {typeKey}");

            if (CorlibAssemblies.Contains(typeKey.AssemblyName) && !_selected.ContainsKey(typeKey))
                AddRequiredCorlibType(typeKey.FullName);

            var node = AddType(type, null, $"surface:{entry}");
            switch (parts[0])
            {
                case "T" when parts.Length == 3:
                    break;
                case "F" when parts.Length == 4:
                {
                    var fields = type.Fields.Where(field => field.Name == parts[3]).ToArray();
                    if (fields.Length != 1)
                        throw new InvalidDataException($"Proxy surface field must resolve uniquely: {entry}");
                    node.Fields.Add(fields[0]);
                    break;
                }
                case "P" or "G" when parts.Length == 4:
                {
                    var properties = type.Properties.Where(property => property.Name == parts[3]).ToArray();
                    if (properties.Length == 1)
                    {
                        node.Properties.Add(properties[0]);
                        var getter = properties[0].GetMethod;
                        var setter = properties[0].SetMethod;
                        if (getter is not null)
                            node.Methods.Add(getter);
                        else if (parts[0] == "G")
                            throw new InvalidDataException($"Getter-only surface property has no getter: {entry}");
                        if (parts[0] == "P")
                        {
                            if (setter is null)
                                throw new InvalidDataException(
                                    $"Read/write proxy surface property has no setter: {entry}");
                            node.Methods.Add(setter);
                        }
                        break;
                    }

                    if (properties.Length != 0)
                        throw new InvalidDataException($"Proxy surface property must resolve uniquely: {entry}");

                    // The PC reference assemblies describe some IL2CPP fields as C# fields while
                    // the Android metadata generator exposes the same runtime slot through the
                    // generated get_*/set_* property facade. A complete P surface is explicitly
                    // read/write, so selecting the PC field is the correct generator input: the
                    // upstream field-accessor pass emits both accessors without inventing a second
                    // native ABI. Getter-only G entries cannot use this fallback because F would
                    // incorrectly add a setter; they remain fail-closed until a read-only field
                    // surface encoding is introduced deliberately.
                    var fields = type.Fields.Where(field => field.Name == parts[3]).ToArray();
                    if (parts[0] == "P" && fields.Length == 1)
                    {
                        node.Fields.Add(fields[0]);
                        break;
                    }

                    if (parts[0] == "G" && fields.Length == 1)
                        throw new InvalidDataException(
                            $"Getter-only proxy surface resolves to a field; use P for its generated read/write facade: {entry}");

                    throw new InvalidDataException($"Proxy surface property must resolve uniquely: {entry}");
                }
                case "RF" when parts.Length == 4:
                {
                    var resolved = FindReflectedFieldInHierarchy(type, parts[3], entry);
                    if (resolved is null)
                    {
                        _unresolvedReflectedMembers.Add(new UnresolvedReflectedMemberRecord(
                            entry,
                            "field absent from target PC metadata; reflection will return null"));
                        break;
                    }
                    var owner = AddType(
                        resolved.DeclaringType,
                        node.Key,
                        $"surface:{entry}:reflected-declaring-type");
                    if (ReferenceEquals(owner, ClosureNode.Missing))
                    {
                        _unresolvedReflectedMembers.Add(new UnresolvedReflectedMemberRecord(
                            entry,
                            "field declaring type is absent from Android metadata"));
                        break;
                    }
                    owner.Fields.Add(resolved.Member);
                    break;
                }
                case "RP" when parts.Length == 4:
                {
                    var resolved = FindReflectedPropertyInHierarchy(type, parts[3], entry);
                    if (resolved is null)
                    {
                        _unresolvedReflectedMembers.Add(new UnresolvedReflectedMemberRecord(
                            entry,
                            "property absent from target PC metadata; reflection will return null"));
                        break;
                    }
                    var owner = AddType(
                        resolved.DeclaringType,
                        node.Key,
                        $"surface:{entry}:reflected-declaring-type");
                    if (ReferenceEquals(owner, ClosureNode.Missing))
                    {
                        _unresolvedReflectedMembers.Add(new UnresolvedReflectedMemberRecord(
                            entry,
                            "property declaring type is absent from Android metadata"));
                        break;
                    }
                    owner.Properties.Add(resolved.Member);
                    if (resolved.Member.GetMethod is { } getter)
                        owner.Methods.Add(getter);
                    if (resolved.Member.SetMethod is { } setter)
                        owner.Methods.Add(setter);
                    break;
                }
                case "RN" when parts.Length == 4:
                {
                    var resolved = FindReflectedMethodInHierarchy(type, parts[3], entry);
                    if (resolved is null)
                    {
                        _unresolvedReflectedMembers.Add(new UnresolvedReflectedMemberRecord(
                            entry,
                            "method absent from target PC metadata; reflection will return null"));
                        break;
                    }
                    var owner = AddType(
                        resolved.DeclaringType,
                        node.Key,
                        $"surface:{entry}:reflected-declaring-type");
                    if (ReferenceEquals(owner, ClosureNode.Missing))
                    {
                        _unresolvedReflectedMembers.Add(new UnresolvedReflectedMemberRecord(
                            entry,
                            "method declaring type is absent from Android metadata"));
                        break;
                    }
                    owner.Methods.Add(resolved.Member);
                    break;
                }
                case "N" when parts.Length == 4:
                {
                    var methods = type.Methods.Where(method => method.Name == parts[3]).ToArray();
                    if (methods.Length != 1)
                        throw new InvalidDataException(
                            $"Proxy surface reflected method name must resolve uniquely: {entry} " +
                            $"(matches={methods.Length})");
                    node.Methods.Add(methods[0]);
                    break;
                }
                case "M" or "MM" when parts.Length == 8:
                {
                    var identity = MethodIdentity.Parse(parts);
                    var methods = type.Methods.Where(method => MethodIdentity.From(method) == identity).ToArray();
                    if (methods.Length != 1)
                        throw new InvalidDataException($"Proxy surface method must resolve uniquely: {entry}");
                    node.Methods.Add(methods[0]);
                    if (parts[0] == "MM")
                        node.ManagedMethods.Add(methods[0]);
                    break;
                }
                default:
                    throw new InvalidDataException($"Invalid proxy surface entry: {entry}");
            }
        }
    }

    // Type.GetField/GetProperty/GetMethod search public inherited members. The scanner records the
    // receiver's static type from MOD IL, while the generator must retain the member on the type
    // that actually declares it. Resolve each level independently: a derived declaration hides a
    // base declaration, but ambiguity within one declaring type stays fail-closed.
    private static ReflectedMemberResolution<FieldDefinition>? FindReflectedFieldInHierarchy(
        TypeDefinition type,
        string memberName,
        string entry)
        => FindReflectedMemberInHierarchy(
            type,
            memberName,
            entry,
            "field",
            candidate => candidate.Fields,
            candidate => candidate.Name?.ToString());

    private static ReflectedMemberResolution<PropertyDefinition>? FindReflectedPropertyInHierarchy(
        TypeDefinition type,
        string memberName,
        string entry)
        => FindReflectedMemberInHierarchy(
            type,
            memberName,
            entry,
            "property",
            candidate => candidate.Properties,
            candidate => candidate.Name?.ToString());

    private static ReflectedMemberResolution<MethodDefinition>? FindReflectedMethodInHierarchy(
        TypeDefinition type,
        string memberName,
        string entry)
        => FindReflectedMemberInHierarchy(
            type,
            memberName,
            entry,
            "method",
            candidate => candidate.Methods,
            candidate => candidate.Name?.ToString());

    private static ReflectedMemberResolution<TMember>? FindReflectedMemberInHierarchy<TMember>(
        TypeDefinition type,
        string memberName,
        string entry,
        string memberKind,
        Func<TypeDefinition, IEnumerable<TMember>> getMembers,
        Func<TMember, string?> getName)
        where TMember : class
    {
        var visited = new HashSet<TypeDefinition>();
        for (var current = type; current is not null; current = current.BaseType?.Resolve())
        {
            if (!visited.Add(current))
                throw new InvalidDataException(
                    $"Inheritance cycle while resolving reflected {memberKind}: {entry}");

            var matches = getMembers(current)
                .Where(member => string.Equals(getName(member), memberName, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0)
                continue;
            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"Reflected {memberKind} name is ambiguous in target PC metadata: {entry} " +
                    $"(declaringType={current.FullName}, matches={matches.Length})");
            }
            return new ReflectedMemberResolution<TMember>(current, matches[0]);
        }

        return null;
    }

    private void AddRequiredCorlibType(string fullName)
    {
        var key = CorlibAssemblies
            .Select(assembly => TypeKey.Create(assembly, fullName))
            .FirstOrDefault(_pcTypes.ContainsKey);
        if (key == default)
            throw new InvalidOperationException($"Required PC corlib type is missing: {fullName}");
        if (_selected.ContainsKey(key))
            return;
        var type = _pcTypes[key];
        var node = new ClosureNode(key, type, null, "generator-corlib-scaffold", MemberMode.Skeleton);
        _selected.Add(key, node);
        _pending.Enqueue(node);
    }

    private AndroidTypeRecord ResolveSeed(string seed)
    {
        var separator = seed.IndexOf('|');
        if (separator >= 0)
        {
            var key = TypeKey.Create(seed[..separator], seed[(separator + 1)..]);
            if (!_androidTypes.TryGetValue(key, out var exact))
                throw new InvalidOperationException($"Seed does not exist in Android metadata: {key}");
            return exact.Count == 1
                ? exact[0]
                : throw new InvalidOperationException($"Seed is ambiguous in Android metadata: {key} ({exact.Count} matches)");
        }

        var fullMatches = _androidTypes.Values.SelectMany(types => types)
            .Where(type => string.Equals(
                TypeKey.NormalizeTypeName(type.FullName),
                TypeKey.NormalizeTypeName(seed),
                StringComparison.Ordinal))
            .ToArray();
        var matches = fullMatches.Length != 0
            ? fullMatches
            : _androidTypesByName.GetValueOrDefault(seed)?.ToArray() ?? Array.Empty<AndroidTypeRecord>();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"Seed does not exist in Android metadata: {seed}"),
            _ => throw new InvalidOperationException(
                $"Seed is ambiguous; use assembly|full-name: {seed} -> " +
                string.Join(", ", matches.Select(type => TypeKey.Create(type.AssemblyName, type.FullName))))
        };
    }

    private void ScanNode(ClosureNode node)
    {
        var type = node.Type;
        AddDescriptor(type.BaseType, node.Key, $"{type.FullName}:base");
        AddDelegateRuntimeMembers(node);
        // A PC class can implement Unity-internal interfaces that do not exist in the
        // Android player build. They are implementation details, not part of the
        // explicitly requested proxy surface. Interface inheritance remains part of
        // the public type contract and must stay dependency-closed.
        if (type.IsInterface)
        foreach (var implementation in type.Interfaces)
            AddDescriptor(implementation.Interface, node.Key, $"{type.FullName}:interface");
        ScanGenericParameters(type.GenericParameters, node.Key, $"{type.FullName}:generic");

        var fields = node.Mode == MemberMode.Layout
            ? type.Fields.Where(field => !field.IsStatic).Concat(node.Fields).Distinct()
            : node.Fields;
        foreach (var field in fields)
            AddSignature(field.Signature?.FieldType, node.Key, $"{type.FullName}:field:{field.Name}");

        foreach (var property in node.Properties)
        {
            AddSignature(property.Signature?.ReturnType, node.Key, $"{type.FullName}:property:{property.Name}:return");
            if (property.Signature is not null)
            foreach (var parameter in property.Signature.ParameterTypes)
                AddSignature(parameter, node.Key, $"{type.FullName}:property:{property.Name}:parameter");
        }

        foreach (var method in node.Methods)
        {
            if (method.Signature is not null)
            {
                AddSignature(method.Signature.ReturnType, node.Key, $"{type.FullName}:method:{method.Name}:return");
                foreach (var parameter in method.Signature.ParameterTypes)
                    AddSignature(parameter, node.Key, $"{type.FullName}:method:{method.Name}:parameter");
            }
            ScanGenericParameters(method.GenericParameters, node.Key, $"{type.FullName}:method:{method.Name}:generic");
        }
    }

    private void ScanGenericParameters(IEnumerable<GenericParameter> parameters, TypeKey rootKey, string reason)
    {
        foreach (var parameter in parameters)
        foreach (var constraint in parameter.Constraints)
            AddDescriptor(constraint.Constraint, rootKey, reason + ":constraint");
    }

    private void AddDelegateRuntimeMembers(ClosureNode node)
    {
        // DelegateSupport.ConvertDelegateCore constructs delegate proxies reflectively via
        // Activator.CreateInstance(type, target, methodPtr) on Unity 2021.2+ (see
        // UnityVersionHandler.MustUseDelegateConstructor). The (Object, IntPtr) constructor is
        // invisible to the explicit surface scan; every delegate in the closure must keep it,
        // together with Invoke, or managed-to-native delegate conversion fails at runtime with
        // MissingMethodException.
        var type = node.Type;
        if (!string.Equals(type.BaseType?.FullName, "System.MulticastDelegate", StringComparison.Ordinal))
            return;

        foreach (var method in type.Methods)
        {
            if (method is { IsConstructor: true, IsStatic: false } &&
                method.Parameters.Count == 2 &&
                method.Parameters[0].ParameterType.FullName == "System.Object" &&
                method.Parameters[1].ParameterType.FullName == "System.IntPtr")
            {
                node.Methods.Add(method);
            }
            else if (!method.IsStatic && string.Equals(method.Name?.Value, "Invoke", StringComparison.Ordinal))
            {
                node.Methods.Add(method);
            }
        }
    }

    private void AddDescriptor(ITypeDefOrRef? descriptor, TypeKey rootKey, string reason)
        => AddSignature(descriptor?.ToTypeSignature(), rootKey, reason);

    private void AddSignature(TypeSignature? signature, TypeKey rootKey, string reason)
    {
        if (signature is null || signature is GenericParameterSignature or SentinelTypeSignature)
            return;

        if (signature is GenericInstanceTypeSignature genericInstance)
        {
            AddDescriptor(genericInstance.GenericType, rootKey, reason + ":generic-type");
            foreach (var argument in genericInstance.TypeArguments)
                AddSignature(argument, rootKey, reason + ":generic-argument");
            return;
        }

        if (signature is CustomModifierTypeSignature customModifier)
            AddDescriptor(customModifier.ModifierType, rootKey, reason + ":modifier");

        if (signature is TypeSpecificationSignature specification)
        {
            AddSignature(specification.BaseType, rootKey, reason + ":element");
            return;
        }

        var scopeName = TypeKey.NormalizeAssemblyName(signature.Scope?.Name?.ToString() ?? string.Empty);
        var corlibType = CorlibAssemblies
            .Select(assembly => TypeKey.Create(assembly, signature.FullName))
            .FirstOrDefault(_pcTypes.ContainsKey);
        if (CorlibAssemblies.Contains(scopeName) || (scopeName.Length == 0 && corlibType != default))
        {
            if (corlibType != default)
                AddRequiredCorlibType(signature.FullName);
            return;
        }

        var resolved = signature.Resolve();
        if (resolved is null && scopeName.Length != 0)
            _pcTypes.TryGetValue(TypeKey.Create(scopeName, signature.FullName), out resolved);
        if (resolved is not null)
        {
            var resolvedKey = GetTypeKey(resolved);
            if (CorlibAssemblies.Contains(resolvedKey.AssemblyName))
            {
                AddRequiredCorlibType(resolvedKey.FullName);
                return;
            }
            AddType(resolved, rootKey, reason);
            return;
        }

        if (signature is CorLibTypeSignature)
            return;

        var dedup = rootKey + "|" + reason + "|" + signature.FullName;
        if (_unresolvedDedup.Add(dedup))
            _unresolvedMetadata.Add(new UnresolvedTypeRecord(signature.FullName, reason, BuildPath(rootKey)));
    }

    private ClosureNode AddType(TypeDefinition type, TypeKey? parent, string reason)
    {
        var key = GetTypeKey(type);
        if (CorlibAssemblies.Contains(key.AssemblyName))
            return _selected.GetValueOrDefault(key) ?? ClosureNode.Corlib;
        if (string.Equals(key.AssemblyName, "Il2CppDummyDll", StringComparison.OrdinalIgnoreCase))
            return ClosureNode.Corlib;

        if (type.DeclaringType is not null)
            AddType(type.DeclaringType, parent, reason + ":declaring-type");

        if (!HasAndroidType(key, type))
        {
            var dedup = key + "|" + parent + "|" + reason;
            if (_missingDedup.Add(dedup))
            {
                var path = parent is null ? Array.Empty<string>() : BuildPath(parent.Value);
                _missingAndroid.Add(new MissingTypeRecord(
                    key.AssemblyName,
                    key.FullName,
                    reason,
                    path.Append(key.ToString()).ToArray()));
            }
            return ClosureNode.Missing;
        }

        if (_selected.TryGetValue(key, out var existing))
            return existing;

        if (_selected.Count >= _options.MaxTypes)
            throw new InvalidOperationException(
                $"Proxy closure exceeded --max-types {_options.MaxTypes}; last type: {key}");

        var mode = type.IsValueType && !type.IsEnum ? MemberMode.Layout : MemberMode.Skeleton;
        var node = new ClosureNode(key, type, parent, reason, mode);
        _selected.Add(key, node);
        _pending.Enqueue(node);
        return node;
    }

    private bool HasAndroidType(TypeKey key, TypeDefinition sourceType)
    {
        if (_androidTypes.ContainsKey(key))
            return true;
        if (sourceType.DeclaringType is null)
            return false;

        if (!_androidTypesByName.TryGetValue(sourceType.Name ?? string.Empty, out var matches))
            return false;

        return matches.Count(match =>
            string.Equals(
                TypeKey.NormalizeAssemblyName(match.AssemblyName),
                key.AssemblyName,
                StringComparison.OrdinalIgnoreCase)) == 1;
    }

    private string[] BuildPath(TypeKey key)
    {
        var entries = new Stack<string>();
        var current = key;
        while (_selected.TryGetValue(current, out var node))
        {
            entries.Push($"{node.Key} [{node.Reason}]");
            if (node.Parent is null)
                break;
            current = node.Parent.Value;
        }
        return entries.ToArray();
    }

    private static TypeKey GetTypeKey(TypeDefinition type)
        => TypeKey.Create(type.DeclaringModule?.Assembly?.Name?.ToString() ?? string.Empty, type.FullName);

    private static IEnumerable<string> ReadStrictUtf8Lines(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), true, 64 * 1024);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length != 0 && !value.StartsWith("#", StringComparison.Ordinal))
                yield return value;
        }
    }

    private sealed record ReflectedMemberResolution<TMember>(
        TypeDefinition DeclaringType,
        TMember Member)
        where TMember : class;

    internal sealed class ClosureNode
    {
        public static readonly ClosureNode Corlib = new(default, null!, null, "corlib", MemberMode.All);
        public static readonly ClosureNode Missing = new(default, null!, null, "missing", MemberMode.Skeleton);

        public ClosureNode(TypeKey key, TypeDefinition type, TypeKey? parent, string reason, MemberMode mode)
        {
            Key = key;
            Type = type;
            Parent = parent;
            Reason = reason;
            Mode = mode;
        }

        public TypeKey Key { get; }
        public TypeDefinition Type { get; }
        public TypeKey? Parent { get; }
        public string Reason { get; }
        public MemberMode Mode { get; }
        public HashSet<FieldDefinition> Fields { get; } = new();
        public HashSet<MethodDefinition> Methods { get; } = new();
        public HashSet<MethodDefinition> ManagedMethods { get; } = new();
        public HashSet<PropertyDefinition> Properties { get; } = new();
    }

    private sealed class ClosureAssemblyResolver : AssemblyResolverBase
    {
        public ClosureAssemblyResolver() : base(new ByteArrayFileService()) { }
        protected override string? ProbeRuntimeDirectories(AssemblyDescriptor assembly) => null;
        public void AddToCache(AssemblyDefinition assembly) => AddToCache(assembly, assembly);
    }
}

internal sealed record ClosureResult(
    string AndroidCatalogPath,
    string AssemblyDirectory,
    string SeedPath,
    string SurfacePath,
    IReadOnlyList<string> Seeds,
    IReadOnlyList<SelectedTypeRecord> SelectedTypes,
    IReadOnlyList<ClosureBuilder.ClosureNode> Nodes,
    IReadOnlyList<MissingTypeRecord> MissingAndroidTypes,
    IReadOnlyList<UnresolvedTypeRecord> UnresolvedMetadataTypes,
    IReadOnlyList<UnresolvedReflectedMemberRecord> UnresolvedReflectedMembers)
{
    public async Task WriteAsync(CommandLineOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.AllowListOutputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(options.ReportOutputPath)!);

        var allowList = new StringBuilder();
        allowList.AppendLine("# Generated by xphorror ProxyInputClosure. UTF-8, runtime metadata only.");
        foreach (var node in Nodes)
        {
            allowList.Append("T|").Append(node.Key.AssemblyName).Append('|').Append(node.Key.FullName)
                .Append('|').AppendLine(node.Mode.ToString().ToLowerInvariant());
            foreach (var field in node.Fields.OrderBy(field => field.Name?.ToString(), StringComparer.Ordinal))
                allowList.Append("F|").Append(node.Key.AssemblyName).Append('|').Append(node.Key.FullName)
                    .Append('|').AppendLine(field.Name);
            foreach (var property in node.Properties.OrderBy(property => property.Name?.ToString(), StringComparer.Ordinal))
                allowList.Append("P|").Append(node.Key.AssemblyName).Append('|').Append(node.Key.FullName)
                    .Append('|').AppendLine(property.Name);
            foreach (var method in node.Methods.OrderBy(method => MethodIdentity.From(method).ToString(), StringComparer.Ordinal))
                allowList.Append(node.ManagedMethods.Contains(method) ? "MM|" : "M|")
                    .Append(node.Key.AssemblyName).Append('|').Append(node.Key.FullName)
                    .Append('|').AppendLine(MethodIdentity.From(method).ToString());
        }
        await File.WriteAllTextAsync(options.AllowListOutputPath, allowList.ToString(), new UTF8Encoding(false));

        string allowListSha256;
        await using (var stream = File.OpenRead(options.AllowListOutputPath))
            allowListSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        var report = new
        {
            formatVersion = "xphorror.il2cpp-proxy-closure.v3-field-backed-property-surface",
            generatedUtc = DateTime.UtcNow.ToString("O"),
            source = new
            {
                androidCatalogPath = Path.GetFullPath(AndroidCatalogPath),
                pcAssemblyDirectory = Path.GetFullPath(AssemblyDirectory),
                seedPath = Path.GetFullPath(SeedPath),
                surfacePath = Path.GetFullPath(SurfacePath)
            },
            policy = new
            {
                runtimeAddressPolicy = "metadata_only",
                missingTypePolicy = "fail_closed",
                missingReflectedMemberPolicy = "preserve_null_and_report",
                rootMemberScope = "explicit_surface_only",
                referenceDependencyScope = "type_skeleton",
                classInterfaceDependencyScope = "excluded_pc_implementation_detail",
                interfaceInheritanceScope = "dependency_closed",
                valueTypeDependencyScope = "instance_layout_fields",
                corlibScope = "generator_scaffold_only; package_existing_Il2Cppmscorlib"
            },
            allowListSha256,
            summary = new
            {
                seedCount = Seeds.Count,
                selectedTypeCount = SelectedTypes.Count,
                selectedAssemblyCount = SelectedTypes.Select(type => type.AssemblyName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                explicitFieldCount = SelectedTypes.Sum(type => type.ExplicitFieldCount),
                explicitMethodCount = SelectedTypes.Sum(type => type.ExplicitMethodCount),
                explicitPropertyCount = SelectedTypes.Sum(type => type.ExplicitPropertyCount),
                missingAndroidTypeCount = MissingAndroidTypes.Count,
                unresolvedMetadataTypeCount = UnresolvedMetadataTypes.Count,
                unresolvedReflectedMemberCount = UnresolvedReflectedMembers.Count
            },
            seeds = Seeds,
            selectedTypes = SelectedTypes,
            missingAndroidTypes = MissingAndroidTypes,
            unresolvedMetadataTypes = UnresolvedMetadataTypes,
            unresolvedReflectedMembers = UnresolvedReflectedMembers
        };
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = options.Pretty,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        await File.WriteAllTextAsync(
            options.ReportOutputPath,
            JsonSerializer.Serialize(report, jsonOptions),
            new UTF8Encoding(false));
    }
}

internal readonly record struct TypeKey(string AssemblyName, string FullName)
{
    public static TypeKey Create(string assemblyName, string fullName)
        => new(NormalizeAssemblyName(assemblyName), NormalizeTypeName(fullName));
    public override string ToString() => AssemblyName + "|" + FullName;

    public static string NormalizeAssemblyName(string value)
    {
        var result = value.Trim();
        return result.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? result[..^4] : result;
    }

    /// <summary>
    /// Normalizes a type name so the two libraries in this pipeline agree on it.
    /// </summary>
    /// <remarks>
    /// The surface manifest is written by ProxySurfaceScanner (dnlib) and read back here
    /// (AsmResolver). The two render a generic instantiation's argument list differently -
    /// AsmResolver puts a space after each comma, dnlib does not - so
    /// <c>UnityAction`2&lt;Scene, LoadSceneMode&gt;</c> and <c>UnityAction`2&lt;Scene,LoadSceneMode&gt;</c>
    /// are the same method but compared unequal, and the manifest entry then resolved to 0 methods.
    /// Stripping the spaces after commas makes both spellings converge. Nested-type separators
    /// (<c>/</c> vs <c>+</c>) are the same class of divergence and were already handled.
    /// </remarks>
    public static string NormalizeTypeName(string value)
        => value.Trim().Replace('/', '+').Replace(", ", ",");
}

internal readonly record struct MethodIdentity(
    bool IsStatic,
    int GenericArity,
    string ReturnType,
    string Name,
    string ParameterTypes)
{
    public static MethodIdentity From(MethodDefinition method)
        => new(
            method.IsStatic,
            method.GenericParameters.Count,
            TypeKey.NormalizeTypeName(method.Signature?.ReturnType.FullName ?? string.Empty),
            method.Name ?? string.Empty,
            string.Join(";", method.Signature?.ParameterTypes.Select(type => TypeKey.NormalizeTypeName(type.FullName))
                             ?? Array.Empty<string>()));

    public static MethodIdentity Parse(string[] parts)
    {
        if (parts[3] is not ("static" or "instance") ||
            !int.TryParse(parts[4], out var genericArity) || genericArity < 0)
            throw new InvalidDataException("Invalid method identity in proxy surface manifest.");
        return new MethodIdentity(
            parts[3] == "static",
            genericArity,
            TypeKey.NormalizeTypeName(parts[5]),
            parts[6],
            string.Join(";", parts[7].Split(';').Select(TypeKey.NormalizeTypeName)));
    }

    public override string ToString()
        => (IsStatic ? "static" : "instance") + "|" + GenericArity + "|" + ReturnType + "|" + Name + "|" + ParameterTypes;
}

internal enum MemberMode { Skeleton, Layout, All }

internal sealed record SelectedTypeRecord(
    string AssemblyName,
    string FullName,
    string Mode,
    int ExplicitFieldCount,
    int ExplicitMethodCount,
    int ExplicitPropertyCount,
    string SourceAssemblyPath,
    IReadOnlyList<string> DependencyPath);

internal sealed record MissingTypeRecord(
    string AssemblyName,
    string FullName,
    string Reason,
    IReadOnlyList<string> DependencyPath);

internal sealed record UnresolvedTypeRecord(
    string TypeName,
    string Reason,
    IReadOnlyList<string> DependencyPath);

internal sealed record UnresolvedReflectedMemberRecord(
    string Entry,
    string Reason);

internal sealed class AndroidTypeCatalog
{
    public string? FormatVersion { get; init; }
    public IReadOnlyList<AndroidTypeRecord> Types { get; init; } = Array.Empty<AndroidTypeRecord>();
}

internal sealed class AndroidTypeRecord
{
    public required string AssemblyName { get; init; }
    public required string Namespace { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string Kind { get; init; }
    public int Line { get; init; }
}

internal sealed class CommandLineOptions
{
    public required string AssemblyDirectory { get; init; }
    public required string AndroidCatalogPath { get; init; }
    public required string SeedPath { get; init; }
    public required string SurfacePath { get; init; }
    public required string AllowListOutputPath { get; init; }
    public required string ReportOutputPath { get; init; }
    public int MaxTypes { get; init; } = 10_000;
    public bool Pretty { get; init; }

    public static CommandLineOptions? Parse(string[] args)
    {
        string? assemblies = null;
        string? catalog = null;
        string? seeds = null;
        string? surface = null;
        string? allowList = null;
        string? report = null;
        var maxTypes = 10_000;
        var pretty = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--assemblies" when index + 1 < args.Length: assemblies = args[++index]; break;
                case "--android-catalog" when index + 1 < args.Length: catalog = args[++index]; break;
                case "--seed-file" when index + 1 < args.Length: seeds = args[++index]; break;
                case "--surface-file" when index + 1 < args.Length: surface = args[++index]; break;
                case "--allowlist-output" when index + 1 < args.Length: allowList = args[++index]; break;
                case "--report-output" when index + 1 < args.Length: report = args[++index]; break;
                case "--max-types" when index + 1 < args.Length && int.TryParse(args[index + 1], out maxTypes): index++; break;
                case "--pretty": pretty = true; break;
                case "--help" or "-h": PrintUsage(); return null;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[index]}");
                    PrintUsage();
                    return null;
            }
        }

        if (new[] { assemblies, catalog, seeds, surface, allowList, report }.Any(string.IsNullOrWhiteSpace) || maxTypes <= 0)
        {
            PrintUsage();
            return null;
        }

        var result = new CommandLineOptions
        {
            AssemblyDirectory = Path.GetFullPath(assemblies!),
            AndroidCatalogPath = Path.GetFullPath(catalog!),
            SeedPath = Path.GetFullPath(seeds!),
            SurfacePath = Path.GetFullPath(surface!),
            AllowListOutputPath = Path.GetFullPath(allowList!),
            ReportOutputPath = Path.GetFullPath(report!),
            MaxTypes = maxTypes,
            Pretty = pretty
        };
        if (!Directory.Exists(result.AssemblyDirectory))
            throw new DirectoryNotFoundException($"Required closure input is missing: {result.AssemblyDirectory}");
        foreach (var required in new[] { result.AndroidCatalogPath, result.SeedPath, result.SurfacePath })
            if (!File.Exists(required))
                throw new FileNotFoundException($"Required closure input is missing: {required}");
        return result;
    }

    private static void PrintUsage() => Console.WriteLine(
        "ProxyInputClosure --assemblies <dir> --android-catalog <catalog.json> " +
        "--seed-file <types.txt> --surface-file <members.txt> --allowlist-output <types.txt> " +
        "--report-output <report.json> [--max-types <count>] [--pretty]");
}
