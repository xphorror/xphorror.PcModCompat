using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StArray.ModManager.Runtime;

namespace Xphorror.PcModCompat;

internal static class PcCompatModSessionIdentity
{
    private static readonly byte[] ManifestDomain =
        "xphorror.pcmodcompat/manifest-revision/v1"u8.ToArray();

    internal static PcCompatModSessionRequest Create(
        PcModManifest manifest,
        long hostGeneration,
        long resourceGeneration)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (hostGeneration <= 0 || resourceGeneration <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(hostGeneration),
                "PC MOD session generations must be positive.");
        var assemblyDigest = PcCompatUiRecipeBinary.ComputeSourceAssemblySha256(manifest);
        if (assemblyDigest.All(value => value == 0))
            throw new FileNotFoundException(
                $"PC MOD source assembly is unavailable for mod={manifest.Id}.");
        return new PcCompatModSessionRequest(
            manifest.Id,
            hostGeneration,
            resourceGeneration,
            assemblyDigest,
            ComputeManifestRevision(manifest));
    }

    internal static byte[] ComputeManifestRevision(PcModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, ManifestDomain);
        AppendField(hash, Encoding.UTF8.GetBytes(manifest.RawInfoJson ?? string.Empty));
        AppendField(hash, Encoding.UTF8.GetBytes(manifest.RawJAModInfoJson ?? string.Empty));
        return hash.GetHashAndReset();
    }

    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)value.Length));
        hash.AppendData(length);
        hash.AppendData(value);
    }
}

internal sealed record PcCompatModSessionRequest(
    string ModId,
    long HostGeneration,
    long ResourceGeneration,
    byte[] AssemblyDigest,
    byte[] ManifestDigest)
{
    internal bool IsValid =>
        !string.IsNullOrWhiteSpace(ModId) &&
        HostGeneration > 0 &&
        ResourceGeneration > 0 &&
        AssemblyDigest is { Length: 32 } &&
        ManifestDigest is { Length: 32 };
}

internal sealed class PcCompatModSessionLease : IDisposable
{
    private static long s_nextHandle;
    private readonly Action _close;
    private int _disposed;

    internal PcCompatModSessionLease(
        PcCompatModSessionRequest request,
        ulong sessionHandle,
        Action? close = null)
    {
        Request = request;
        SessionHandle = sessionHandle;
        _close = close ?? (() => { });
    }

    internal PcCompatModSessionRequest Request { get; }
    internal ulong SessionHandle { get; }
    internal bool IsActive => Volatile.Read(ref _disposed) == 0;

    internal static PcCompatModSessionLease CreateLocal(PcCompatModSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sequence = unchecked((ulong)Interlocked.Increment(ref s_nextHandle));
        if (sequence == 0)
            sequence = unchecked((ulong)Interlocked.Increment(ref s_nextHandle));
        return new PcCompatModSessionLease(request, sequence * 0x9E3779B185EBCA87UL | 1UL);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _close();
    }
}
