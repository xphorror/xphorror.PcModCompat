using System.Reflection;
using System.Text;

namespace Xphorror.PcModCompat;

public static class PcCompatVirtualAssetDiagnostics
{
    public static string FormatProjectionFailure(
        PcCompatVirtualAssetProjectionRequest request,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);
        return FormatFailure(
            "VirtualBundle asset projection failed",
            request.ModId,
            request.SessionGeneration,
            request.BundleId,
            request.CandidateSha256Hex,
            request.SourceAsset,
            exception,
            $"projectionExpectedType={request.ExpectedType}");
    }

    public static string FormatResolverFailure(
        PcCompatVirtualAssetResolveRequest request,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);

        return FormatFailure(
            "VirtualBundle asset materialization failed",
            request.ModId,
            request.SessionGeneration,
            request.BundleId,
            request.CandidateSha256Hex,
            request.Asset,
            exception,
            extraContext: null);
    }

    private static string FormatFailure(
        string heading,
        string modId,
        long sessionGeneration,
        string bundleId,
        string candidateSha256Hex,
        PcCompatResourceIrAsset asset,
        Exception exception,
        string? extraContext)
    {
        var chain = BuildExceptionChain(exception);
        var rootCause = chain[^1];
        var builder = new StringBuilder(2048);
        builder.AppendLine(heading);
        builder.Append("context modId=").Append(Value(modId))
            .Append(" generation=").Append(sessionGeneration)
            .Append(" bundleId=").Append(Value(bundleId))
            .Append(" candidateSha256=").Append(Value(candidateSha256Hex));
        if (!string.IsNullOrWhiteSpace(extraContext))
            builder.Append(' ').Append(extraContext);
        builder.AppendLine();
        builder.Append("asset id=").Append(Value(asset.Id))
            .Append(" name=").Append(Value(asset.Name))
            .Append(" sourceType=").Append(Value(asset.SourceType))
            .Append(" expectedType=").Append(Value(asset.ExpectedType))
            .Append(" pathId=").Append(asset.PathId)
            .Append(" required=").Append(asset.RequiredByMod)
            .Append(" materializationKind=").Append(asset.MaterializationKind)
            .Append(" payloadId=").Append(Value(asset.PayloadId))
            .Append(" dependencies=").AppendLine(Value(string.Join(',', asset.DependencyIds)));
        builder.AppendLine("exceptionChainBegin");
        for (var index = 0; index < chain.Count; index++)
        {
            builder.Append('[').Append(index).Append("] ")
                .Append(chain[index].GetType().FullName)
                .Append(": ")
                .AppendLine(chain[index].Message);
        }
        builder.AppendLine("exceptionChainEnd");
        builder.AppendLine("rootCauseBegin");
        builder.AppendLine(rootCause.ToString());
        builder.Append("rootCauseEnd");
        return builder.ToString();
    }

    private static IReadOnlyList<Exception> BuildExceptionChain(Exception exception)
    {
        var chain = new List<Exception>();
        var current = exception;
        while (true)
        {
            chain.Add(current);
            if (current is TargetInvocationException { InnerException: not null } invocation)
            {
                current = invocation.InnerException!;
                continue;
            }
            if (current.InnerException == null)
                return chain;
            current = current.InnerException;
        }
    }

    private static string Value(string? value)
        => string.IsNullOrEmpty(value)
            ? "<none>"
            : value.Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);
}
