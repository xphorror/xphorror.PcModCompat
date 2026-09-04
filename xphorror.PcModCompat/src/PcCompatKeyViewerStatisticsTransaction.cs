using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using StArray.ModManager.Runtime;

namespace Xphorror.PcModCompat;

internal readonly record struct PcCompatKeyViewerStatisticsFeature(
    PcCompatKeyViewerFeatureAdapter Adapter,
    PcCompatKeyViewerFeatureOverride Override);

internal sealed class PcCompatKeyViewerStatisticsTransaction
{
    private const int MaxPersistenceFileBytes = 1024 * 1024;
    private const int MaxOverlaySnapshotFiles = 256;
    private const long MaxOverlaySnapshotBytes = 8L * 1024 * 1024;
    private static readonly string[] RequiredFieldRoles =
        ["HeldState", "CountState", "TotalState", "KpsWindow"];
    private static readonly HashSet<string> SnapshotFieldRoles = new(
        [
            "HeldState",
            "CountState",
            "TotalState",
            "KpsWindow",
            "KpsState",
            "PersistencePendingState",
            "PersistenceDirtyState"
        ],
        StringComparer.Ordinal);

    private readonly PcCompatManagedExecutionState _executionState;
    private readonly FieldSnapshot[] _fields;
    private readonly FileSnapshot[] _files;
    private readonly DirectorySnapshot[] _directories;
    private readonly MethodInvocation[] _saveSinks;
    private readonly object _restoreGate = new();
    private bool _restored;

    private PcCompatKeyViewerStatisticsTransaction(
        PcCompatManagedExecutionState executionState,
        FieldSnapshot[] fields,
        FileSnapshot[] files,
        DirectorySnapshot[] directories,
        MethodInvocation[] saveSinks)
    {
        _executionState = executionState;
        _fields = fields;
        _files = files;
        _directories = directories;
        _saveSinks = saveSinks;
    }

    internal static bool TryCreate(
        AssemblyLoadContext loadContext,
        PcModManifest manifest,
        object rootInstance,
        PcCompatManagedExecutionState executionState,
        IReadOnlyList<PcCompatKeyViewerStatisticsFeature> features,
        out PcCompatKeyViewerStatisticsTransaction? transaction,
        out string? error)
    {
        transaction = null;
        error = null;
        if (features.Count == 0)
        {
            error = "no enabled KeyViewer features were supplied";
            return false;
        }

        try
        {
            var fields = new Dictionary<string, FieldSnapshot>(StringComparer.Ordinal);
            var saveSinks = new Dictionary<string, MethodInvocation>(StringComparer.Ordinal);
            var files = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
            var directories = new Dictionary<string, DirectorySnapshot>(StringComparer.OrdinalIgnoreCase);
            using (PcCompatManagedExecutionContext.Enter(executionState))
            {
                foreach (var feature in features)
                {
                    foreach (var requiredRole in RequiredFieldRoles)
                    {
                        if (!ResolveRoles(feature, requiredRole, memberKind: "Field").Any())
                        {
                            error = $"feature '{feature.Adapter.Id}' has no verified {requiredRole} field role";
                            return false;
                        }
                    }

                    foreach (var role in feature.Adapter.Roles
                                 .Where(role => SnapshotFieldRoles.Contains(role.Role) &&
                                                RoleSelected(feature, role)))
                    {
                        if (!TryResolveField(
                                loadContext,
                                rootInstance,
                                role,
                                out var field,
                                out var target,
                                out error))
                            return false;
                        var key = MemberKey(field!);
                        if (!fields.ContainsKey(key))
                        {
                            if (!FieldSnapshot.TryCapture(field!, target, out var snapshot, out error))
                                return false;
                            fields.Add(key, snapshot!);
                        }
                    }

                    var sinkRoles = ResolveRoles(feature, "PersistenceSink", memberKind: "Method")
                        .ToArray();
                    var persistencePath = feature.Adapter.CountSemantics.PersistencePath;
                    if (sinkRoles.Length == 1 && !string.IsNullOrWhiteSpace(persistencePath))
                    {
                        if (!TryResolveMethod(
                                loadContext,
                                rootInstance,
                                sinkRoles[0],
                                out var sink,
                                out var sinkTarget,
                                out error))
                            return false;
                        saveSinks.TryAdd(MemberKey(sink!), new MethodInvocation(sink!, sinkTarget));
                        foreach (var path in new[]
                                 {
                                     persistencePath,
                                     feature.Adapter.CountSemantics.BackupPersistencePath
                                 }.Where(path => !string.IsNullOrWhiteSpace(path)))
                        {
                            if (!TryResolvePersistencePath(manifest.FolderPath, path!, out var fullPath))
                            {
                                error = $"feature '{feature.Adapter.Id}' has an unsafe persistence path";
                                return false;
                            }
                            if (PcCompatManagedPathBridge.IsBound(
                                    executionState.ModId,
                                    executionState.ResourceSessionGeneration))
                            {
                                fullPath = PcCompatManagedPathBridge.ResolveWritablePath(fullPath);
                            }
                            if (!files.ContainsKey(fullPath))
                            {
                                if (!FileSnapshot.TryCapture(fullPath, out var snapshot, out error))
                                    return false;
                                files.Add(fullPath, snapshot!);
                            }
                        }
                        continue;
                    }

                    if (!IsVerifiedOwnerOverlayFallback(feature.Adapter))
                    {
                        error = sinkRoles.Length != 1
                            ? $"feature '{feature.Adapter.Id}' must resolve exactly one PersistenceSink"
                            : $"feature '{feature.Adapter.Id}' has no persistence path";
                        return false;
                    }
                    if (!PcCompatManagedPathBridge.TryGetDataOverlayRoot(
                            executionState.ModId,
                            executionState.ResourceSessionGeneration,
                            out var overlayRoot))
                    {
                        error = $"feature '{feature.Adapter.Id}' has no owner-scoped data overlay";
                        return false;
                    }
                    if (!directories.ContainsKey(overlayRoot))
                    {
                        if (!DirectorySnapshot.TryCapture(overlayRoot, out var snapshot, out error))
                        {
                            error = $"feature '{feature.Adapter.Id}' overlay snapshot failed: {error}";
                            return false;
                        }
                        directories.Add(overlayRoot, snapshot!);
                    }
                }
            }

            transaction = new PcCompatKeyViewerStatisticsTransaction(
                executionState,
                fields.Values.ToArray(),
                files.Values.ToArray(),
                directories.Values.ToArray(),
                saveSinks.Values.ToArray());
            return true;
        }
        catch (Exception exception)
        {
            error = Describe(exception);
            return false;
        }
    }

    internal bool TryRestore(out string? error)
    {
        lock (_restoreGate)
        {
            error = null;
            if (_restored)
                return true;
            try
            {
                using (PcCompatManagedExecutionContext.Enter(_executionState))
                {
                    foreach (var field in _fields)
                        field.Restore();
                    foreach (var file in _files)
                        file.Restore();
                    foreach (var directory in _directories)
                        directory.Restore();
                    foreach (var sink in _saveSinks)
                        sink.Invoke();
                }
                _restored = true;
                return true;
            }
            catch (Exception exception)
            {
                error = Describe(exception);
                return false;
            }
        }
    }

    private static bool IsVerifiedOwnerOverlayFallback(PcCompatKeyViewerFeatureAdapter feature)
    {
        var capabilities = feature.Capabilities;
        return feature.Backend == PcCompatKeyViewerBackend.ManagedSelfRender &&
               capabilities.Input.Status == PcCompatAdapterEvidenceStatus.Proven &&
               capabilities.Lane.Status == PcCompatAdapterEvidenceStatus.Proven &&
               capabilities.Transition.Status == PcCompatAdapterEvidenceStatus.Proven &&
               capabilities.Count.Status == PcCompatAdapterEvidenceStatus.Proven;
    }

    private static IEnumerable<PcCompatKeyViewerRoleBinding> ResolveRoles(
        PcCompatKeyViewerStatisticsFeature feature,
        string roleName,
        string memberKind)
        => feature.Adapter.Roles.Where(role =>
            string.Equals(role.Role, roleName, StringComparison.Ordinal) &&
            string.Equals(role.MemberKind, memberKind, StringComparison.OrdinalIgnoreCase) &&
            RoleSelected(feature, role));

    private static bool RoleSelected(
        PcCompatKeyViewerStatisticsFeature feature,
        PcCompatKeyViewerRoleBinding role)
    {
        var selected = feature.Override.Roles
            .Where(candidate => string.Equals(candidate.Role, role.Role, StringComparison.Ordinal))
            .ToArray();
        return selected.Length == 0 || selected.Any(candidate =>
            string.Equals(candidate.CandidateKey,
                PcCompatKeyViewerOverrideStore.GetCandidateKey(
                    role.AssemblyName,
                    role.TypeName,
                    role.MemberName,
                    role.MemberKind),
                StringComparison.Ordinal));
    }

    private static bool TryResolveField(
        AssemblyLoadContext loadContext,
        object rootInstance,
        PcCompatKeyViewerRoleBinding role,
        out FieldInfo? field,
        out object? target,
        out string? error)
    {
        field = null;
        target = null;
        if (!TryResolveType(loadContext, role, out var type, out error))
            return false;
        field = type!.GetField(
            role.MemberName!,
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (field == null)
        {
            error = $"field '{role.TypeName}.{role.MemberName}' was not found";
            return false;
        }
        target = field.IsStatic ? null : ResolveTarget(type, rootInstance);
        if (!field.IsStatic && target == null)
        {
            error = $"instance target for '{role.TypeName}.{role.MemberName}' is unavailable";
            return false;
        }
        return true;
    }

    private static bool TryResolveMethod(
        AssemblyLoadContext loadContext,
        object rootInstance,
        PcCompatKeyViewerRoleBinding role,
        out MethodInfo? method,
        out object? target,
        out string? error)
    {
        method = null;
        target = null;
        if (!TryResolveType(loadContext, role, out var type, out error))
            return false;
        var methods = type!.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(candidate => candidate.Name == role.MemberName &&
                                candidate.ReturnType == typeof(void) &&
                                candidate.GetParameters().Length == 0)
            .ToArray();
        if (methods.Length != 1)
        {
            error = $"method '{role.TypeName}.{role.MemberName}()' resolved {methods.Length} candidates";
            return false;
        }
        method = methods[0];
        target = method.IsStatic ? null : ResolveTarget(type, rootInstance);
        if (!method.IsStatic && target == null)
        {
            error = $"instance target for '{role.TypeName}.{role.MemberName}()' is unavailable";
            return false;
        }
        return true;
    }

    private static bool TryResolveType(
        AssemblyLoadContext loadContext,
        PcCompatKeyViewerRoleBinding role,
        out Type? type,
        out string? error)
    {
        var assembly = loadContext.Assemblies.FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, role.AssemblyName,
                StringComparison.OrdinalIgnoreCase));
        type = assembly?.GetType(role.TypeName, throwOnError: false, ignoreCase: false);
        error = type == null
            ? $"type '{role.TypeName}' was not found in assembly '{role.AssemblyName}'"
            : null;
        return type != null;
    }

    private static object? ResolveTarget(Type type, object rootInstance)
    {
        if (type.IsInstanceOfType(rootInstance))
            return rootInstance;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Static;
        if (type.GetField("Instance", flags)?.GetValue(null) is { } fieldValue &&
            type.IsInstanceOfType(fieldValue))
            return fieldValue;
        if (type.GetProperty("Instance", flags)?.GetValue(null) is { } propertyValue &&
            type.IsInstanceOfType(propertyValue))
            return propertyValue;
        return null;
    }

    private static bool TryResolvePersistencePath(
        string root,
        string relativePath,
        out string fullPath)
    {
        root = Path.GetFullPath(root);
        fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string MemberKey(MemberInfo member)
        => $"{member.Module.ModuleVersionId:N}:{member.MetadataToken:X8}";

    private static string Describe(Exception exception)
    {
        var root = exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception.GetBaseException();
        return $"{root!.GetType().Name}: {root.Message}";
    }

    private sealed class FieldSnapshot
    {
        private readonly FieldInfo _field;
        private readonly object? _target;
        private readonly object? _value;
        private readonly SnapshotKind _kind;

        private FieldSnapshot(FieldInfo field, object? target, object? value, SnapshotKind kind)
        {
            _field = field;
            _target = target;
            _value = value;
            _kind = kind;
        }

        internal static bool TryCapture(
            FieldInfo field,
            object? target,
            out FieldSnapshot? snapshot,
            out string? error)
        {
            snapshot = null;
            error = null;
            var value = field.GetValue(target);
            if (value is Array queueArray && queueArray.Rank == 1 &&
                field.FieldType.GetElementType() is { IsGenericType: true } elementType &&
                elementType.GetGenericTypeDefinition() == typeof(Queue<>))
            {
                object?[]?[] values = new object?[queueArray.Length][];
                for (var index = 0; index < queueArray.Length; ++index)
                {
                    values[index] = queueArray.GetValue(index) is IEnumerable queue
                        ? queue.Cast<object?>().ToArray()
                        : null;
                }
                snapshot = new FieldSnapshot(field, target, values, SnapshotKind.QueueArray);
                return true;
            }
            if (value is Array array && array.Rank == 1 &&
                IsStableElementType(field.FieldType.GetElementType()))
            {
                snapshot = new FieldSnapshot(field, target, array.Clone(), SnapshotKind.Array);
                return true;
            }
            if (field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(Queue<>) && value != null)
            {
                snapshot = new FieldSnapshot(
                    field,
                    target,
                    ((IEnumerable)value).Cast<object?>().ToArray(),
                    SnapshotKind.Queue);
                return true;
            }
            if (!field.IsInitOnly && IsStableElementType(field.FieldType))
            {
                snapshot = new FieldSnapshot(field, target, value, SnapshotKind.Scalar);
                return true;
            }
            error = $"field '{field.DeclaringType?.FullName}.{field.Name}' has unsupported " +
                    $"transaction type '{field.FieldType.FullName}'";
            return false;
        }

        internal void Restore()
        {
            switch (_kind)
            {
                case SnapshotKind.Scalar:
                    _field.SetValue(_target, _value);
                    return;
                case SnapshotKind.Array:
                {
                    var source = (Array)_value!;
                    var destination = _field.GetValue(_target) as Array;
                    if (destination == null || destination.Rank != 1 ||
                        destination.Length != source.Length)
                    {
                        if (_field.IsInitOnly)
                        {
                            throw new InvalidOperationException(
                                $"readonly array field '{_field.Name}' changed during playback");
                        }
                        _field.SetValue(_target, source.Clone());
                        return;
                    }
                    Array.Copy(source, destination, source.Length);
                    return;
                }
                case SnapshotKind.Queue:
                {
                    var queue = _field.GetValue(_target)
                        ?? throw new InvalidOperationException(
                            $"queue field '{_field.Name}' became null during playback");
                    var clear = queue.GetType().GetMethod("Clear", Type.EmptyTypes)!;
                    var enqueue = queue.GetType().GetMethod("Enqueue")!;
                    clear.Invoke(queue, null);
                    foreach (var item in (object?[])_value!)
                        enqueue.Invoke(queue, [item]);
                    return;
                }
                case SnapshotKind.QueueArray:
                {
                    var source = (object?[]?[])_value!;
                    var destination = _field.GetValue(_target) as Array
                        ?? throw new InvalidOperationException(
                            $"queue array field '{_field.Name}' became null during playback");
                    if (destination.Rank != 1 || destination.Length != source.Length)
                        throw new InvalidOperationException(
                            $"queue array field '{_field.Name}' changed length during playback");
                    for (var index = 0; index < source.Length; ++index)
                    {
                        var baseline = source[index];
                        var queue = destination.GetValue(index);
                        if (baseline == null)
                        {
                            if (queue != null)
                                destination.SetValue(null, index);
                            continue;
                        }
                        queue ??= Activator.CreateInstance(destination.GetType().GetElementType()!)
                            ?? throw new InvalidOperationException(
                                $"queue array field '{_field.Name}' element {index} cannot be recreated");
                        destination.SetValue(queue, index);
                        var clear = queue.GetType().GetMethod("Clear", Type.EmptyTypes)!;
                        var enqueue = queue.GetType().GetMethod("Enqueue")!;
                        clear.Invoke(queue, null);
                        foreach (var item in baseline)
                            enqueue.Invoke(queue, [item]);
                    }
                    return;
                }
            }
        }

        private static bool IsStableElementType(Type? type)
            => type != null && (type.IsPrimitive || type.IsEnum || type == typeof(decimal) ||
                                type == typeof(string));

        private enum SnapshotKind
        {
            Scalar,
            Array,
            Queue,
            QueueArray
        }
    }

    private sealed class DirectorySnapshot
    {
        private readonly string _root;
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly IReadOnlySet<string> _directories;

        private DirectorySnapshot(
            string root,
            IReadOnlyDictionary<string, byte[]> files,
            IReadOnlySet<string> directories)
        {
            _root = root;
            _files = files;
            _directories = directories;
        }

        internal static bool TryCapture(
            string root,
            out DirectorySnapshot? snapshot,
            out string? error)
        {
            snapshot = null;
            error = null;
            root = Path.GetFullPath(root);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
            if (!Directory.Exists(root))
            {
                snapshot = new DirectorySnapshot(root, files, directories);
                return true;
            }

            long totalBytes = 0;
            foreach (var directory in Directory.EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"data overlay contains a link: {directory}";
                    return false;
                }
                directories.Add(Path.GetRelativePath(root, directory));
            }
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (files.Count >= MaxOverlaySnapshotFiles)
                {
                    error = $"data overlay exceeds {MaxOverlaySnapshotFiles} files";
                    return false;
                }
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"data overlay contains a linked file: {path}";
                    return false;
                }
                totalBytes = checked(totalBytes + info.Length);
                if (info.Length > MaxPersistenceFileBytes || totalBytes > MaxOverlaySnapshotBytes)
                {
                    error = $"data overlay exceeds the bounded snapshot budget at {path}";
                    return false;
                }
                files.Add(Path.GetRelativePath(root, path), File.ReadAllBytes(path));
            }
            snapshot = new DirectorySnapshot(root, files, directories);
            return true;
        }

        internal void Restore()
        {
            Directory.CreateDirectory(_root);
            foreach (var path in Directory.EnumerateFiles(
                         _root,
                         "*",
                         SearchOption.AllDirectories).ToArray())
            {
                var relative = Path.GetRelativePath(_root, path);
                if (!_files.ContainsKey(relative))
                    File.Delete(path);
            }
            foreach (var (relative, content) in _files)
            {
                var path = Path.GetFullPath(Path.Combine(_root, relative));
                if (!ModDataDomainPaths.IsWithin(_root, path))
                    throw new InvalidOperationException("data overlay snapshot escaped its owner root");
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                var temporary = Path.Combine(
                    directory,
                    $".{Path.GetFileName(path)}.starray-{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(temporary, content);
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
            foreach (var directory in Directory.EnumerateDirectories(
                         _root,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .ToArray())
            {
                var relative = Path.GetRelativePath(_root, directory);
                if (!_directories.Contains(relative) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
    }

    private sealed class FileSnapshot
    {
        private readonly string _path;
        private readonly byte[]? _content;

        private FileSnapshot(string path, byte[]? content)
        {
            _path = path;
            _content = content;
        }

        internal static bool TryCapture(
            string path,
            out FileSnapshot? snapshot,
            out string? error)
        {
            snapshot = null;
            error = null;
            if (!File.Exists(path))
            {
                snapshot = new FileSnapshot(path, null);
                return true;
            }
            var info = new FileInfo(path);
            if (info.Length > MaxPersistenceFileBytes)
            {
                error = $"persistence file '{path}' exceeds {MaxPersistenceFileBytes} bytes";
                return false;
            }
            snapshot = new FileSnapshot(path, File.ReadAllBytes(path));
            return true;
        }

        internal void Restore()
        {
            if (_content == null)
            {
                if (File.Exists(_path))
                    File.Delete(_path);
                return;
            }
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.starray-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, _content);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
    }

    private readonly record struct MethodInvocation(MethodInfo Method, object? Target)
    {
        internal void Invoke() => Method.Invoke(Target, null);
    }
}
