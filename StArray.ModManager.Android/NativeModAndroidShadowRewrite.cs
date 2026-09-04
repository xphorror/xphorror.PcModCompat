using StArray.ModManager.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android;

internal static class NativeModAndroidShadowRewrite
{
    private const string RewriteAbi =
        NativeModIsolationRewriteApi.FormatVersion +
        "-callback-only-v3-logical-assembly-location";

    /// <summary>
    /// Native MODs keep their original process-global and IO semantics. Only legacy managed
    /// HookHelper.Hook callbacks are wrapped so a native thread can re-enter the owning MOD
    /// generation before executing its delegate.
    /// </summary>
    internal static void Install()
    {
        NativeModShadowRewriteRuntime.RegisterProvider(RewriteAbi, Rewrite);
    }

    private static NativeModShadowRewriteResult Rewrite(NativeModShadowRewriteRequest request)
    {
        var bridgeAssembly = typeof(NativeModPathBridge).Assembly;
        var bridgeAssemblyName = bridgeAssembly.GetName().Name
            ?? throw new InvalidOperationException("Native MOD bridge assembly has no name.");
        var bridgeAssemblyPath = PcCompatManagedAssemblyRewrite.ResolveRuntimeAssemblyPath(
            bridgeAssembly.Location,
            bridgeAssemblyName,
            AppContext.BaseDirectory);
        var report = NativeModIsolationRewriteApi.Rewrite(
            request.InputAssemblyPath,
            request.OutputAssemblyPath,
            bridgeAssemblyPath,
            request.PrivateAssemblyPaths,
            new NativeModIsolationRewriteOptions
            {
                Mode = NativeModIsolationRewriteMode.CallbackOnly,
                StrongNamePolicy = NativeModStrongNameRewritePolicy.PreserveIdentityWithoutResigning
            });

        return new NativeModShadowRewriteResult(
            report.RewrittenAssemblyLocationCalls +
            report.RewrittenStaticFieldInstructions +
            report.RewrittenAsyncCalls +
            report.TrackedTaskReturnMethods +
            report.RewrittenFileCalls +
            report.RewrittenNetworkCalls,
            report.Issues)
        {
            StaticSlots = report.StaticSlots
                .Select(slot => new NativeModShadowStaticSlotRecord(
                    slot.StaticSlotId,
                    slot.MemberIdentity))
                .ToArray(),
            AsyncRewrites = report.AsyncRewrites
                .Select(rewrite => new NativeModShadowAsyncRewriteRecord(
                    rewrite.MemberIdentity,
                    rewrite.Kind,
                    rewrite.RewriteCount))
                .ToArray(),
            FileRewrites = report.FileRewrites
                .Select(rewrite => new NativeModShadowFileRewriteRecord(
                    rewrite.MemberIdentity,
                    rewrite.Kind,
                    rewrite.RewriteCount))
                .ToArray(),
            NetworkRewrites = report.NetworkRewrites
                .Select(rewrite => new NativeModShadowNetworkRewriteRecord(
                    rewrite.MemberIdentity,
                    rewrite.Kind,
                    rewrite.RewriteCount))
                .ToArray()
        };
    }
}
