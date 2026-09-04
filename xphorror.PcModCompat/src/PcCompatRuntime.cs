using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using System.Runtime.ExceptionServices;

namespace Xphorror.PcModCompat;

public sealed class PcCompatPreparedMod
{
    public required PcModManifest Manifest { get; init; }
    public required PcCompatStaticPatchScanReport StaticScan { get; init; }
    public required PcCompatCallbackTranslationReport CallbackTranslation { get; init; }
    public required bool HasRecipe { get; init; }
    public PcCompatRecipeCompileReport? RecipeReport { get; init; }
    public string? RecipeError { get; init; }
    public PcCompatResourceCompileInfo? ResourceCompileInfo { get; init; }
    public string? ResourceCompileError { get; init; }
    public PcCompatManagedAssemblyBundleInfo? ManagedAssemblyBundle { get; init; }
    public string? ManagedAssemblyError { get; init; }
}

public enum PcCompatManagedFrameDispatchMode
{
    Disabled = 0,
    PendingActivation = 1,
    Active = 2
}

public static class PcCompatRuntime
{
    public const string LoaderKind = "xphorror.PcModCompat";
    private const string LogTag = "PcModCompat";
    private const string SettingsRuntimeRevision = "settings-frame-lane-v2";
    private const int UnityMainUnloadStartTimeoutMilliseconds = 5_000;
    private static readonly object SessionLock = new();
    private static readonly object ManagedDispatchLifecycleLock = new();
    [ThreadStatic]
    private static int s_managedDispatchDepth;
    private static readonly Dictionary<string, PcCompatManagedModSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatModSessionLease> NativeSessions =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, OwnedRuntimeBinding> OwnedRuntimeBindings =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatRecipeCompileReport> RecipeReports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatRecipeBundleInfo> RecipeBundles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatStaticPatchScanReport> StaticScanReports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatCallbackTranslationReport> CallbackTranslationReports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatManagedAssemblyBundleInfo> ManagedAssemblyBundles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ManagedAssemblyErrors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PcCompatResourceCompileInfo> ResourceCompileInfos = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> ResourceCompileErrors = new(StringComparer.OrdinalIgnoreCase);
    private static Action<PcCompatManagedFrameDispatchMode>? s_managedFrameGateSink;
    private static Action<bool>? s_managedOnGUIGateSink;
    private static Func<string, bool, bool>? s_managedPresentationOwnershipSink;
    private static Func<string, bool>? s_nativeRuleBundleRetireSink;
    private static PcCompatManagedPrefixOrderPlanHandler? s_managedPrefixOrderPlanSink;
    private static PcCompatManagedPostfixOrderPlanHandler? s_managedPostfixOrderPlanSink;
    private static Func<bool>? s_managedInstallContextProbe;
    private static Func<bool>? s_unityMainThreadProbe;
    private static Func<Action, bool>? s_unityMainWorkScheduler;
    private static int s_managedFrameGateState = -1;
    private static int s_managedOnGUIGateState = -1;
    private static int s_managedFrameDispatchActive;
    private static int s_managedOnGUIDispatchActive;
    private static readonly PcCompatManagedEventDispatchCollector ManagedEventCollector = new();

    private sealed class ManagedDispatchSnapshot(
        PcCompatManagedModSession[] frameSessions,
        PcCompatManagedModSession[] onGuiSessions,
        IReadOnlyDictionary<string, PcCompatManagedModSession> prefixSessions)
    {
        public PcCompatManagedModSession[] FrameSessions { get; } = frameSessions;
        public PcCompatManagedModSession[] OnGuiSessions { get; } = onGuiSessions;
        public IReadOnlyDictionary<string, PcCompatManagedModSession> PrefixSessions { get; } =
            prefixSessions;
    }

    private static ManagedDispatchSnapshot s_managedDispatchSnapshot = new(
        Array.Empty<PcCompatManagedModSession>(),
        Array.Empty<PcCompatManagedModSession>(),
        new Dictionary<string, PcCompatManagedModSession>(StringComparer.OrdinalIgnoreCase));

    public static PcCompatPatchRegistry PatchRegistry { get; } = new();
    public static event Action? RegistryChanged;

    public static bool TryResolveManagedIntSequence(
        string modId,
        PcCompatKeyViewerRoleOverride role,
        int requiredCount,
        out int[] values,
        out string? error)
    {
        PcCompatManagedModSession? session;
        lock (SessionLock)
            Sessions.TryGetValue(modId, out session);
        if (session == null)
        {
            values = Array.Empty<int>();
            error = $"managed session '{modId}' is unavailable";
            return false;
        }
        return session.TryResolveManagedIntSequence(role, requiredCount, out values, out error);
    }

    internal static bool TryBeginKeyViewerStatisticsTransaction(
        string modId,
        IReadOnlyList<PcCompatKeyViewerStatisticsFeature> features,
        out PcCompatKeyViewerStatisticsTransaction? transaction,
        out string? error)
    {
        PcCompatManagedModSession? session;
        lock (SessionLock)
            Sessions.TryGetValue(modId, out session);
        if (session == null)
        {
            transaction = null;
            error = null;
            return true;
        }
        return session.TryBeginKeyViewerStatisticsTransaction(
            features,
            out transaction,
            out error);
    }

    public static bool TryProjectManagedKeyViewerLabels(
        string modId,
        PcCompatKeyViewerRoleOverride role,
        IReadOnlyList<string> labels,
        bool adoptLegacyTouchLabels,
        out int changed,
        out string? error)
    {
        PcCompatManagedModSession? session;
        lock (SessionLock)
            Sessions.TryGetValue(modId, out session);
        if (session == null)
        {
            changed = 0;
            error = $"managed session '{modId}' is unavailable";
            return false;
        }
        return session.TryProjectManagedKeyViewerLabels(
            role,
            labels,
            adoptLegacyTouchLabels,
            out changed,
            out error);
    }

    public static bool TryRestoreManagedKeyViewerLabels(
        string modId,
        PcCompatKeyViewerRoleOverride role,
        int laneCount,
        out int changed,
        out string? error)
    {
        PcCompatManagedModSession? session;
        lock (SessionLock)
            Sessions.TryGetValue(modId, out session);
        if (session == null)
        {
            changed = 0;
            error = $"managed session '{modId}' is unavailable";
            return false;
        }
        return session.TryRestoreManagedKeyViewerLabels(
            role,
            laneCount,
            out changed,
            out error);
    }

    internal static void RegisterMod(
        PcModManifest manifest,
        ModRuntimeSession? runtimeSession = null,
        ModRuntimeKey runtimeKey = default)
    {
        var prepared = PrepareMod(manifest);
        RegisterPreparedMod(prepared, runtimeSession, runtimeKey);
    }

    public static PcCompatPreparedMod PrepareMod(
        PcModManifest manifest,
        Action<float, string>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        reportProgress?.Invoke(0.05f, "Scanning PATCH metadata");
        cancellationToken.ThrowIfCancellationRequested();
        var staticScan = PcCompatStaticPatchScanner.Scan(manifest, GetTargetGameRevision());

        reportProgress?.Invoke(0.45f, "Translating callbacks");
        cancellationToken.ThrowIfCancellationRequested();
        var callbackTranslation = PcCompatCallbackTranslator.Translate(manifest, staticScan);

        PcCompatResourceCompileInfo? resourceCompileInfo = null;
        string? resourceCompileError = null;
        if (PcCompatResourceAssemblyCompile.IsProviderRegistered)
        {
            reportProgress?.Invoke(0.6f, "Indexing MOD resources");
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                resourceCompileInfo = PcCompatResourceAssemblyCompile.Prepare(
                    manifest,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                resourceCompileError = exception.ToString();
                Logger.Warn(
                    LogTag,
                    $"resource compile unavailable mod={manifest.Id}; managed/rule compilation will continue: " +
                    exception.Message);
            }
        }

        reportProgress?.Invoke(0.72f, "Rewriting managed assembly");
        cancellationToken.ThrowIfCancellationRequested();
        PcCompatManagedAssemblyBundleInfo? managedAssemblyBundle = null;
        string? managedAssemblyError = null;
        try
        {
            managedAssemblyBundle = PcCompatManagedAssemblyRewrite.Prepare(
                manifest,
                staticScan,
                cancellationToken);
            if (managedAssemblyBundle == null)
            {
                throw new InvalidOperationException(
                    PcCompatManagedAssemblyRewrite.IsProviderRegistered
                        ? "managed rewrite provider returned no bundle"
                        : "managed rewrite provider is not registered");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            managedAssemblyError = exception.ToString();
            Logger.Warn(
                LogTag,
                $"managed rewrite unavailable mod={manifest.Id}; recipe compilation will continue: " +
                exception.Message);
        }

        reportProgress?.Invoke(0.88f, "Compiling native rules");
        cancellationToken.ThrowIfCancellationRequested();
        var hasRecipe = PcCompatRecipeCompiler.TryCompile(
            manifest,
            staticScan,
            callbackTranslation,
            out var recipeReport,
            out var recipeError);

        reportProgress?.Invoke(0.96f, "Waiting for main-thread install");
        return new PcCompatPreparedMod
        {
            Manifest = manifest,
            StaticScan = staticScan,
            CallbackTranslation = callbackTranslation,
            HasRecipe = hasRecipe,
            RecipeReport = recipeReport,
            RecipeError = recipeError,
            ResourceCompileInfo = resourceCompileInfo,
            ResourceCompileError = resourceCompileError,
            ManagedAssemblyBundle = managedAssemblyBundle,
            ManagedAssemblyError = managedAssemblyError
        };
    }

    internal static void RegisterPreparedMod(
        PcCompatPreparedMod prepared,
        ModRuntimeSession? runtimeSession = null,
        ModRuntimeKey runtimeKey = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        EnsureManagedInstallContext();
        var manifest = prepared.Manifest;
        UnregisterMod(manifest);

        lock (SessionLock)
        {
            if (prepared.ResourceCompileInfo is { } resourceInfo)
            {
                ResourceCompileInfos[manifest.Id] = resourceInfo;
                ResourceCompileErrors.Remove(manifest.Id);
            }
            else
            {
                ResourceCompileInfos.Remove(manifest.Id);
                if (!string.IsNullOrWhiteSpace(prepared.ResourceCompileError))
                    ResourceCompileErrors[manifest.Id] = prepared.ResourceCompileError;
                else
                    ResourceCompileErrors.Remove(manifest.Id);
            }
        }
        if (prepared.ResourceCompileInfo is { } compiledResource)
        {
            Logger.Info(
                LogTag,
                $"resource compile mod={manifest.Id} cache={compiledResource.CacheHit} " +
                $"compatibility={compiledResource.Compatibility} " +
                $"candidates={compiledResource.CandidateCount} groups={compiledResource.FeatureGroupCount} " +
                $"bindings={compiledResource.BindingCount} irBundles={compiledResource.IrBundleCount} " +
                $"irAssets={compiledResource.IrAssetCount} irRequired={compiledResource.IrRequiredAssetCount}");
        }

        Logger.Info(LogTag, $"register mod id={manifest.Id} kind={manifest.Kind} entry={manifest.AssemblyName} method={manifest.EntryMethod}");
        if (prepared.ManagedAssemblyBundle is { } managedBundle)
        {
            lock (SessionLock)
            {
                ManagedAssemblyBundles[manifest.Id] = managedBundle;
                ManagedAssemblyErrors.Remove(manifest.Id);
            }
            Logger.Info(
                LogTag,
                $"managed rewrite mod={manifest.Id} cache={managedBundle.CacheKey} hit={managedBundle.CacheHit} " +
                $"rewritten={managedBundle.RewrittenInstructions} bridge={managedBundle.ManagedBridgeRewrites} " +
                $"passthrough={managedBundle.PassthroughInstructions}");
        }
        else if (!string.IsNullOrWhiteSpace(prepared.ManagedAssemblyError))
        {
            lock (SessionLock)
            {
                ManagedAssemblyBundles.Remove(manifest.Id);
                ManagedAssemblyErrors[manifest.Id] = prepared.ManagedAssemblyError;
            }
            Logger.Warn(
                LogTag,
                $"managed rewrite capability unavailable mod={manifest.Id}: " +
                FirstLine(prepared.ManagedAssemblyError));
        }

        if (manifest.Kind == PcModKind.JAMod)
        {
            Logger.Info(LogTag, $"JAMod metadata class={manifest.JAModClassName ?? "<none>"} assembly={manifest.JAModAssemblyPath ?? "<none>"} requireModPath={manifest.AssemblyRequireModPath}");
        }

        var staticScan = prepared.StaticScan;
        lock (SessionLock)
            StaticScanReports[manifest.Id] = staticScan;
        WriteStaticScanReport(manifest, staticScan);
        Logger.Info(
            LogTag,
            $"static scan mod={manifest.Id} assemblies={staticScan.AssembliesScanned.Count} patches={staticScan.Patches.Count} active-r{staticScan.TargetGameRevision}={staticScan.ActivePatches.Count} issues={staticScan.Issues.Count}");

        var callbackTranslation = prepared.CallbackTranslation;
        lock (SessionLock)
            CallbackTranslationReports[manifest.Id] = callbackTranslation;
        WriteCallbackTranslationReport(manifest, callbackTranslation);
        Logger.Info(
            LogTag,
            $"callback translation mod={manifest.Id} rules={callbackTranslation.Rules.Count} translated={callbackTranslation.TranslatedCount} unsupported={callbackTranslation.UnsupportedCount}");
        var requiresManagedSynchronousPrefix = callbackTranslation.Items.Any(item =>
            item.PatchKind == PcCompatPatchKind.Prefix &&
            item.Status == PcCompatCallbackTranslationStatus.Translated &&
            item.ManagedDispatchRequired);

        var hasRecipe = prepared.HasRecipe;
        var recipeReport = prepared.RecipeReport;
        var recipeError = prepared.RecipeError;

        // A render-callback rule exists only to forward a Unity callback into the MOD's own managed
        // code, so it cannot be served by the verified-fixed-op path. Without this, emitting the rule
        // would flip hasRecipe true for a MOD that has no other rules - JipperKeyViewer uses no
        // Harmony at all - and the branch below would report it "loaded from verified rule recipe"
        // while never running its managed setup.
        var requiresManagedRenderDispatch = recipeReport?.Rules.Any(rule =>
            rule.Op == PcCompatRuleOp.ManagedRenderCallback) == true;
        if (requiresManagedRenderDispatch)
            requiresManagedSynchronousPrefix = true;

        if (hasRecipe)
        {
            if (recipeReport == null)
                throw new InvalidOperationException($"Recipe compiler returned success without a report for mod={manifest.Id}.");
            lock (SessionLock)
                RecipeReports[manifest.Id] = recipeReport;

            PublishRecipeBundle(manifest, recipeReport);
            Logger.Info(LogTag, $"recipe={recipeReport.RecipeId} mod={manifest.Id} compatibility={recipeReport.Compatibility} features={recipeReport.Features.Count} rules={recipeReport.Rules.Count} unsupported={recipeReport.Unsupported.Count}");
        }
        else
        {
            lock (SessionLock)
                RecipeReports.Remove(manifest.Id);

            Logger.Info(LogTag, $"no verified rule recipe for mod={manifest.Id}: {recipeError}");
        }

        // Resource recipes are independent of UI fixed-op success. Publishing the
        // session plan is read-only and never LoadFromFile's.
        TryPublishResourceSession(manifest, GetRecipeBundle(manifest.Id));

        var runRewrittenOracle = EnvEnabled(
            "STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE",
#if STARRAY_PCMOD_COMPAT_REWRITTEN_ORACLE_DEFAULT
            true);
#else
            false);
#endif
        // Self-render generalization: mods without a verified rule recipe are
        // no longer rejected; they fall back to the rewritten managed session
        // and self-render by default so arbitrary UMM/Harmony mods can load.
        // Mods that do own a recipe keep the verified fixed-op path unless the
        // env opt-in (or the per-mod UI request) asks for managed self-render.
        // STARRAY_PCMOD_COMPAT_RECIPE_ONLY restores the old throw-on-no-recipe
        // behavior for recipe verification runs.
        var runManagedSelfRender =
            requiresManagedSynchronousPrefix ||
            ((EnvEnabled("STARRAY_PCMOD_COMPAT_SELF_RENDER", false) || !hasRecipe) &&
             !EnvEnabled("STARRAY_PCMOD_COMPAT_RECIPE_ONLY", false));
        var useRewrittenAssembly = runRewrittenOracle || runManagedSelfRender;

        if (!hasRecipe && !useRewrittenAssembly)
        {
            RegistryChanged?.Invoke();
            throw new NotSupportedException(recipeError ?? $"No verified rule recipe is available for mod id={manifest.Id}");
        }

        PcCompatModSessionLease pcModSession;
        try
        {
            pcModSession = OpenPcModSession(manifest, runtimeKey);
            RegisterOwnedSessionResources(
                manifest,
                runtimeSession,
                runtimeKey,
                pcModSession);
        }
        catch
        {
            ClosePcModSession(manifest.Id);
            PcCompatVirtualBundleRegistry.RemoveMod(manifest.Id);
            PcCompatResourceRecipeRuntime.Unload(manifest.Id);
            PcCompatResourceChangerRuntime.Remove(manifest.Id);
            throw;
        }

        if (hasRecipe && !useRewrittenAssembly)
        {
            Logger.Info(LogTag, $"mod={manifest.Id} loaded from verified rule recipe; managed PC setup skipped");
            RegistryChanged?.Invoke();
            return;
        }

        var shimFolder = PcCompatManagedLoader.ResolveShimFolder(manifest);
        if (shimFolder == null)
        {
            ClosePcModSession(manifest.Id);
            throw new DirectoryNotFoundException($"PcModCompat shim folder not found for mod {manifest.Id}");
        }

        if (useRewrittenAssembly && prepared.ManagedAssemblyBundle is null)
        {
            ClosePcModSession(manifest.Id);
            throw new InvalidOperationException(
                $"Rewritten managed execution requested without a valid rewrite bundle for mod={manifest.Id}: " +
                (FirstLine(prepared.ManagedAssemblyError) ?? "rewrite provider produced no bundle"));
        }

        PcCompatManagedModSession session;
        try
        {
            session = PcCompatManagedLoader.Load(manifest, new PcCompatLoadOptions
            {
                ShimFolder = shimFolder,
                TargetAssemblyPath = useRewrittenAssembly
                    ? prepared.ManagedAssemblyBundle!.RewrittenAssemblyPath
                    : null,
                BootstrapAssemblyPath = useRewrittenAssembly &&
                                        prepared.ManagedAssemblyBundle!.BootstrapAssemblyName is { } bootstrapName &&
                                        prepared.ManagedAssemblyBundle.RewrittenAssemblyPaths.TryGetValue(
                                            bootstrapName,
                                            out var bootstrapPath)
                    ? bootstrapPath
                    : null,
                RewrittenAssemblyPaths = useRewrittenAssembly
                    ? prepared.ManagedAssemblyBundle!.RewrittenAssemblyPaths
                    : null,
                ProxyFolder = useRewrittenAssembly
                    ? Path.Combine(AppContext.BaseDirectory, "pc_compat_proxies")
                    : null,
                TryBootstrap = true,
                Enable = false,
                RuntimeSession = runtimeSession,
                RuntimeKey = runtimeKey,
                PcModSession = pcModSession
            });
        }
        catch
        {
            ClosePcModSession(manifest.Id);
            throw;
        }

        foreach (var patch in session.RegisteredPatches)
            PatchRegistry.Register(patch);

        if (runManagedSelfRender)
        {
            session.RequestActivation();
            Logger.Info(
                LogTag,
                $"mod={manifest.Id} managed_self_render=pending " +
                $"resourceGeneration={session.ResourceSessionGeneration}");
        }

        lock (SessionLock)
            Sessions[manifest.Id] = session;
        UpdateManagedFrameGate();

        Logger.Info(LogTag, $"mod={manifest.Id} setup={session.SetupCompleted} bootstrapAttempted={session.BootstrapAttempted} bootstrapSucceeded={session.BootstrapSucceeded} patches={session.RegisteredPatches.Count}");
        Logger.Warn(LogTag, $"mod={manifest.Id} status=registered_only reason=native hook bridge will synchronize if available");
        RegistryChanged?.Invoke();
    }

    internal static void RegisterManagedInstallContextProbe(Func<bool>? probe)
        => Volatile.Write(ref s_managedInstallContextProbe, probe);

    internal static void RegisterUnityMainThreadProbe(Func<bool>? probe)
        => Volatile.Write(ref s_unityMainThreadProbe, probe);

    internal static void RegisterUnityMainWorkScheduler(Func<Action, bool>? scheduler)
        => Volatile.Write(ref s_unityMainWorkScheduler, scheduler);

    internal static bool TryScheduleInteropOnUnityMain(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var threadProbe = Volatile.Read(ref s_unityMainThreadProbe);
        if (threadProbe?.Invoke() == true)
        {
            work();
            return true;
        }
        return Volatile.Read(ref s_unityMainWorkScheduler)?.Invoke(work) == true;
    }

    private static void EnsureManagedInstallContext()
    {
        var probe = Volatile.Read(ref s_managedInstallContextProbe);
        if (probe != null && !probe())
        {
            throw new InvalidOperationException(
                "PcCompat managed installation must run inside the platform UnityMain finalization callback.");
        }
    }

    internal static void UnregisterMod(PcModManifest manifest)
    {
        if (s_managedDispatchDepth != 0)
            throw new InvalidOperationException(
                $"Cannot unload mod={manifest.Id} from its managed frame/OnGUI callback.");

        var contextProbe = Volatile.Read(ref s_managedInstallContextProbe);
        if (contextProbe != null && !contextProbe())
        {
            var threadProbe = Volatile.Read(ref s_unityMainThreadProbe);
            if (threadProbe != null && threadProbe())
            {
                Logger.Info(
                    LogTag,
                    $"[DEBUG-kv-unload-v1] unitymain-unregister-inline mod={manifest.Id} " +
                    $"tid={Environment.CurrentManagedThreadId}");
                using var unityMain = PcCompatUnityMainExecutionContext.Enter();
                lock (ManagedDispatchLifecycleLock)
                    UnregisterModCore(manifest);
                return;
            }
            UnregisterModOnUnityMain(manifest);
            return;
        }

        lock (ManagedDispatchLifecycleLock)
            UnregisterModCore(manifest);
    }

    private static void UnregisterModOnUnityMain(PcModManifest manifest)
    {
        var scheduler = Volatile.Read(ref s_unityMainWorkScheduler)
                        ?? throw new InvalidOperationException(
                            $"Cannot unload mod={manifest.Id}: UnityMain work scheduler is unavailable.");
        using var completion = new ManualResetEventSlim(false);
        ExceptionDispatchInfo? failure = null;
        var workState = 0; // 0=pending, 1=running, 2=cancelled, 3=complete

        void Run()
        {
            if (Interlocked.CompareExchange(ref workState, 1, 0) != 0)
                return;
            try
            {
                if (!PcCompatUnityMainExecutionContext.IsActive)
                {
                    throw new InvalidOperationException(
                        $"Scheduled unload for mod={manifest.Id} ran outside UnityMain.");
                }
                Logger.Info(
                    LogTag,
                    $"[DEBUG-kv-unload-v1] unitymain-unregister-run mod={manifest.Id} " +
                    $"tid={Environment.CurrentManagedThreadId}");
                lock (ManagedDispatchLifecycleLock)
                    UnregisterModCore(manifest);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Volatile.Write(ref workState, 3);
                completion.Set();
            }
        }

        Logger.Info(
            LogTag,
            $"[DEBUG-kv-unload-v1] unitymain-unregister-queue mod={manifest.Id} " +
            $"tid={Environment.CurrentManagedThreadId}");
        if (!scheduler(Run))
        {
            throw new InvalidOperationException(
                $"Cannot unload mod={manifest.Id}: UnityMain work queue rejected the transaction.");
        }

        if (!completion.Wait(UnityMainUnloadStartTimeoutMilliseconds))
        {
            if (Interlocked.CompareExchange(ref workState, 2, 0) == 0)
            {
                throw new TimeoutException(
                    $"Cannot unload mod={manifest.Id}: UnityMain did not start the transaction within " +
                    $"{UnityMainUnloadStartTimeoutMilliseconds} ms.");
            }
            completion.Wait();
        }
        failure?.Throw();
    }

    private static void UnregisterModCore(PcModManifest manifest)
    {
        Logger.Info(
            LogTag,
            $"[DEBUG-kv-unload-v1] runtime-unregister-enter mod={manifest.Id} " +
            $"unityMain={PcCompatUnityMainExecutionContext.IsActive} " +
            $"tid={Environment.CurrentManagedThreadId}");
        // Stop concurrent platform reconciliation from republishing this bundle
        // while the native retire sink waits for an in-progress load operation.
        PcCompatRecipeBundleInfo? retiringBundle = null;
        lock (SessionLock)
        {
            if (RecipeBundles.Remove(manifest.Id, out var existingBundle))
                retiringBundle = existingBundle;
        }
        if (!RetireNativeRuleBundle(manifest.Id))
        {
            if (retiringBundle != null)
            {
                lock (SessionLock)
                    RecipeBundles[manifest.Id] = retiringBundle;
            }
            throw new InvalidOperationException(
                $"Native rule bundle retirement failed for mod={manifest.Id}; managed session preserved.");
        }
        Logger.Info(
            LogTag,
            $"[DEBUG-kv-unload-v1] native-retire-complete mod={manifest.Id} " +
            $"tid={Environment.CurrentManagedThreadId}");
        PcCompatKeyViewerLabelProjectionRuntime.Unregister(manifest.Id);
        PcCompatManagedModSession? session = null;
        lock (SessionLock)
        {
            if (Sessions.Remove(manifest.Id, out var existing))
                session = existing;
        }

        if (session?.ManagedPresentationClaimed == true)
            SetManagedPresentationOwnership(session, managedOwnsPresentation: false);
        PublishManagedPrefixOrderPlan(manifest.Id, Array.Empty<PcCompatManagedPrefixOrderEntry>());
        PublishManagedPostfixOrderPlan(manifest.Id, Array.Empty<PcCompatManagedPostfixOrderEntry>());
        Logger.Info(
            LogTag,
            $"[DEBUG-kv-unload-v1] session-dispose-enter mod={manifest.Id} " +
            $"present={session != null} generation={session?.ResourceSessionGeneration ?? 0} " +
            $"unityMain={PcCompatUnityMainExecutionContext.IsActive} " +
            $"tid={Environment.CurrentManagedThreadId}");
        session?.Dispose();
        Logger.Info(
            LogTag,
            $"[DEBUG-kv-unload-v1] session-dispose-complete mod={manifest.Id} " +
            $"tid={Environment.CurrentManagedThreadId}");
        UpdateManagedFrameGate();
        PcCompatResourceChangerRuntime.TryDisable(manifest.Id);
        PatchRegistry.RemoveMod(manifest.Id);
        PcCompatVirtualBundleRegistry.RemoveMod(manifest.Id);
        PcCompatResourceRecipeRuntime.Unload(manifest.Id);
        PcCompatResourceChangerRuntime.Remove(manifest.Id);
        PcCompatKeyViewerLoweredConsumerPlanRegistry.Remove(manifest.Id);
        ClosePcModSession(manifest.Id);
        lock (SessionLock)
        {
            RecipeReports.Remove(manifest.Id);
            RecipeBundles.Remove(manifest.Id);
            StaticScanReports.Remove(manifest.Id);
            CallbackTranslationReports.Remove(manifest.Id);
            ManagedAssemblyBundles.Remove(manifest.Id);
            ManagedAssemblyErrors.Remove(manifest.Id);
            ResourceCompileInfos.Remove(manifest.Id);
            ResourceCompileErrors.Remove(manifest.Id);
        }
        Logger.Info(LogTag, $"unregister mod id={manifest.Id}");
        Logger.Info(
            LogTag,
            $"[DEBUG-kv-unload-v1] runtime-unregister-complete mod={manifest.Id} " +
            $"tid={Environment.CurrentManagedThreadId}");
        RegistryChanged?.Invoke();
    }

    private static PcCompatModSessionLease OpenPcModSession(
        PcModManifest manifest,
        ModRuntimeKey runtimeKey)
    {
        var hostGeneration = runtimeKey.IsValid ? runtimeKey.Generation : 1;
        if (!PcCompatResourceRecipeRuntime.TryGetSessionGeneration(
                manifest.Id, out var resourceGeneration) ||
            resourceGeneration <= 0)
        {
            // MODs without a resource recipe still need a non-zero resource lane
            // identity. It follows the Host load generation and therefore retires
            // with the same reload transaction.
            resourceGeneration = hostGeneration;
        }
        var request = PcCompatModSessionIdentity.Create(
            manifest, hostGeneration, resourceGeneration);
        var lease = PcCompatModSessionLease.CreateLocal(request);
        lock (SessionLock)
        {
            if (NativeSessions.Remove(manifest.Id, out var previous))
                previous.Dispose();
            NativeSessions[manifest.Id] = lease;
        }
        Logger.Info(
            LogTag,
            $"native session mod={manifest.Id} hostGeneration={hostGeneration} " +
            $"resourceGeneration={resourceGeneration} handle=0x{lease.SessionHandle:X}");
        return lease;
    }

    private static void RegisterOwnedSessionResources(
        PcModManifest manifest,
        ModRuntimeSession? runtimeSession,
        ModRuntimeKey runtimeKey,
        PcCompatModSessionLease lease)
    {
        if (runtimeSession == null || !runtimeKey.IsValid)
            return;
        if (!runtimeSession.CanRegisterOwnedResource(runtimeKey))
        {
            throw new InvalidOperationException(
                $"PC MOD capability session belongs to an inactive runtime generation " +
                $"mod={manifest.Id} generation={runtimeKey.Generation}.");
        }

        var identities = new List<(ModOwnedResourceKind Kind, string Identity)>
        {
            (
                ModOwnedResourceKind.Provider,
                $"pcmod-capability-session;host-generation={runtimeKey.Generation};" +
                $"resource-generation={lease.Request.ResourceGeneration};")
        };
        if (PcCompatResourceRecipeRuntime.TryGetSessionGeneration(
                manifest.Id,
                out var resourceGeneration) &&
            resourceGeneration > 0)
        {
            identities.Add((
                ModOwnedResourceKind.Resource,
                $"resource-session;generation={resourceGeneration};" +
                $"virtual={(PcCompatVirtualBundleRegistry.HasSession(manifest.Id, resourceGeneration) ? 1 : 0)};"));
        }

        foreach (var (kind, identity) in identities)
        {
            if (ModOwnedResourceRegistry.TryRegister(runtimeKey, kind, identity))
                continue;
            ModOwnedResourceRegistry.Retire(runtimeKey);
            throw new InvalidOperationException(
                $"PC MOD owned resource registration failed mod={manifest.Id} " +
                $"generation={runtimeKey.Generation} kind={kind}.");
        }

        lock (SessionLock)
        {
            OwnedRuntimeBindings[manifest.Id] = new OwnedRuntimeBinding(
                runtimeKey,
                lease.Request.ResourceGeneration);
        }
    }

    internal static bool TryRegisterOwnedResource(
        string modId,
        long resourceGeneration,
        ModOwnedResourceKind kind,
        string identity)
    {
        OwnedRuntimeBinding binding;
        lock (SessionLock)
        {
            if (!OwnedRuntimeBindings.TryGetValue(modId, out binding))
            {
                // Unit/legacy execution has no protected native session and keeps
                // the pre-ownership behavior. A live native session without a
                // binding is a host transaction defect and must fail closed.
                return !NativeSessions.ContainsKey(modId);
            }
        }
        return binding.ResourceGeneration == resourceGeneration &&
               ModOwnedResourceRegistry.TryRegister(binding.Key, kind, identity);
    }

    internal static void RetireOwnedResource(
        string modId,
        long resourceGeneration,
        ModOwnedResourceKind kind,
        string identity)
    {
        OwnedRuntimeBinding binding;
        lock (SessionLock)
        {
            if (!OwnedRuntimeBindings.TryGetValue(modId, out binding) ||
                binding.ResourceGeneration != resourceGeneration)
            {
                return;
            }
        }
        ModOwnedResourceRegistry.RetireMatching(
            binding.Key,
            kind,
            identity);
    }

    private static void ClosePcModSession(string modId)
    {
        PcCompatModSessionLease? lease = null;
        lock (SessionLock)
        {
            OwnedRuntimeBindings.Remove(modId);
            if (NativeSessions.Remove(modId, out var existing))
                lease = existing;
        }
        lease?.Dispose();
    }

    private readonly record struct OwnedRuntimeBinding(
        ModRuntimeKey Key,
        long ResourceGeneration);

    internal static bool IsPcModSessionActive(string modId)
    {
        PcCompatModSessionLease? lease;
        lock (SessionLock)
            NativeSessions.TryGetValue(modId, out lease);
        return lease?.IsActive == true;
    }

    public static IReadOnlyList<PcCompatManagedModSession> SnapshotSessions()
    {
        lock (SessionLock)
            return Sessions.Values.ToArray();
    }

    public static PcCompatManagedModSession? GetManagedSession(string modId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        PcCompatManagedModSession? session;
        lock (SessionLock)
            Sessions.TryGetValue(modId, out session);
        return session != null && IsPcModSessionActive(modId) ? session : null;
    }

    public static bool TryApplyManagedResourceChangerSettings(
        string modId,
        bool changeRabbit,
        bool changeBallColor,
        bool changeTileColor,
        out string? error)
    {
        var session = GetManagedSession(modId);
        if (session == null)
        {
            error = "managed MOD session is unavailable";
            return false;
        }
        return session.TryApplyResourceChangerSettings(
            changeRabbit,
            changeBallColor,
            changeTileColor,
            out error);
    }

    public static bool TryOpenOriginalSettings(string modId, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        var session = GetManagedSession(modId);
        if (session == null)
        {
            error = "managed session is not loaded";
            return false;
        }
        if (!session.RequestSettingsOpen(out error))
        {
            Logger.Warn(
                LogTag,
                $"mod={modId} settings_surface=request_open_rejected " +
                $"revision={SettingsRuntimeRevision} " +
                $"state={session.Settings.State} " +
                $"lifecycle={session.Lifecycle.State} " +
                $"error={ManagedErrorSummary(error)}");
            return false;
        }

        UpdateManagedFrameGate();
        Logger.Info(
            LogTag,
            $"mod={modId} settings_surface=request_open " +
            $"revision={SettingsRuntimeRevision} " +
            $"state={session.Settings.State} " +
            $"lifecycle={session.Lifecycle.State} " +
            $"frameDemand={session.RequiresFrameDispatch} " +
            $"onGuiDemand={session.RequiresOnGUIDispatch}");
        error = null;
        return true;
    }

    public static void RequestCloseOriginalSettings(string modId)
    {
        var session = GetManagedSession(modId);
        session?.RequestSettingsClose();
        UpdateManagedFrameGate();
        if (session != null)
        {
            Logger.Info(
                LogTag,
                $"mod={modId} settings_surface=request_close " +
                $"revision={SettingsRuntimeRevision} " +
                $"state={session.Settings.State} " +
                $"lifecycle={session.Lifecycle.State}");
        }
    }

    public static PcCompatManagedSettingsSnapshot SnapshotOriginalSettings(string modId)
    {
        var session = GetManagedSession(modId);
        return session?.Settings ?? new PcCompatManagedSettingsSnapshot
        {
            State = PcCompatManagedSettingsState.Unavailable,
            Fault = "managed session is not loaded",
            Supported = false
        };
    }

    public static PcCompatManagedSettingsSchemaSnapshot SnapshotSettingsSchema(string modId)
    {
        var session = GetManagedSession(modId);
        return session?.SettingsSchema ?? new PcCompatManagedSettingsSchemaSnapshot
        {
            ModId = modId,
            Error = "managed session is not loaded"
        };
    }

    public static bool RequestSettingsSchemaValue(
        string modId,
        string revision,
        string path,
        string value,
        out string? error)
    {
        var session = GetManagedSession(modId);
        if (session == null)
        {
            error = "managed session is not loaded";
            return false;
        }
        if (!session.RequestSettingsSchemaValue(revision, path, value, out error))
            return false;
        UpdateManagedFrameGate();
        return true;
    }

    public static void RequestSettingsSchemaSaveRetry(string modId)
    {
        GetManagedSession(modId)?.RequestSettingsSchemaSaveRetry();
        UpdateManagedFrameGate();
    }

    internal static bool CanDispatchManagedContinuation(
        PcCompatManagedExecutionState owner)
    {
        if (PcCompatManagedExecutionContext.HasReusableCallback(owner))
            return owner.Phase is not PcCompatManagedExecutionPhase.Disable;

        var sessions = Volatile.Read(ref s_managedDispatchSnapshot).PrefixSessions;
        sessions.TryGetValue(owner.ModId, out var session);
        if (session == null ||
            session.ResourceSessionGeneration != owner.ResourceSessionGeneration)
            return false;

        var state = session.LifecycleState;
        return owner.Phase switch
        {
            PcCompatManagedExecutionPhase.Bootstrap or
            PcCompatManagedExecutionPhase.Setup =>
                state is PcCompatManagedLifecycleState.Loaded or
                    PcCompatManagedLifecycleState.Enabled,
            PcCompatManagedExecutionPhase.Enable or
            PcCompatManagedExecutionPhase.Update =>
                state == PcCompatManagedLifecycleState.Enabled ||
                (owner.Phase == PcCompatManagedExecutionPhase.Enable &&
                 session.IsCurrentEnableExecution(owner)),
            _ => false
        };
    }

    internal static bool TryEnterManagedSessionCallback(
        PcCompatManagedExecutionState owner,
        out IDisposable? lease,
        out CancellationToken retirementToken)
    {
        lease = null;
        retirementToken = new CancellationToken(canceled: true);
        if (PcCompatManagedExecutionContext.TryGetReusableCallback(
                owner,
                out lease,
                out retirementToken))
            return true;

        var sessions = Volatile.Read(ref s_managedDispatchSnapshot).PrefixSessions;
        sessions.TryGetValue(owner.ModId, out var session);
        if (session == null ||
            session.ResourceSessionGeneration != owner.ResourceSessionGeneration ||
            !session.TryEnterRuntimeCallback(out lease))
            return false;
        retirementToken = session.CallbackRetirementToken;
        return true;
    }

    internal static IDisposable? TryEnterManagedExternalCallbackScope(
        PcCompatManagedExecutionState owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!TryEnterManagedSessionCallback(owner, out var lease, out var retirementToken))
            return null;
        IDisposable? unityMainScope = null;
        PcCompatManagedCallbackScope? callbackScope = null;
        try
        {
            callbackScope = PcCompatManagedExecutionContext.EnterCallback(
                owner,
                lease!,
                retirementToken);
            var threadProbe = Volatile.Read(ref s_unityMainThreadProbe);
            if (!PcCompatUnityMainExecutionContext.IsActive &&
                threadProbe?.Invoke() == true)
            {
                unityMainScope =
                    PcCompatUnityMainExecutionContext.EnterExternalCallback();
            }
            return new ManagedExternalCallbackScope(
                PcCompatManagedExecutionContext.Enter(owner),
                unityMainScope,
                callbackScope);
        }
        catch
        {
            unityMainScope?.Dispose();
            if (callbackScope != null)
                callbackScope.Dispose();
            else
                lease?.Dispose();
            throw;
        }
    }

    private sealed class ManagedExternalCallbackScope(
        PcCompatManagedExecutionScope executionScope,
        IDisposable? unityMainScope,
        PcCompatManagedCallbackScope callbackScope) : IDisposable
    {
        private PcCompatManagedCallbackScope? _callbackScope = callbackScope;
        private IDisposable? _unityMainScope = unityMainScope;
        private PcCompatManagedExecutionScope _executionScope = executionScope;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                _executionScope.Dispose();
            }
            finally
            {
                try
                {
                    Interlocked.Exchange(ref _unityMainScope, null)?.Dispose();
                }
                finally
                {
                    _callbackScope?.Dispose();
                    _callbackScope = null;
                }
            }
        }
    }

    internal static T InvokeDynamicGetterOnUnityMain<T>(
        PcCompatManagedExecutionState owner,
        string operation,
        Func<T> callback,
        CancellationToken retirementToken,
        IDisposable callbackLease)
        => InvokeDynamicGetterOnUnityMain(
            owner,
            operation,
            callback,
            retirementToken,
            callbackLease,
            UnityMainUnloadStartTimeoutMilliseconds);

    internal static T InvokeDynamicGetterOnUnityMain<T>(
        PcCompatManagedExecutionState owner,
        string operation,
        Func<T> callback,
        CancellationToken retirementToken,
        IDisposable callbackLease,
        int timeoutMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(callbackLease);
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        if (PcCompatUnityMainExecutionContext.IsActive)
        {
            using (callbackLease)
                return callback();
        }

        var scheduler = Volatile.Read(ref s_unityMainWorkScheduler);
        if (scheduler == null)
        {
            callbackLease.Dispose();
            throw new InvalidOperationException(
                $"PcCompat dynamic getter UnityMain scheduler is unavailable: {operation}.");
        }
        var completion = new ManualResetEventSlim(false);
        ExceptionDispatchInfo? failure = null;
        T result = default!;
        var workState = 0;
        void Run()
        {
            if (Interlocked.CompareExchange(ref workState, 1, 0) != 0)
                return;
            try
            {
                if (callbackLease is IPcCompatManagedCallbackLease transferable)
                    transferable.TransferToCurrentThread();
                if (!PcCompatUnityMainExecutionContext.IsActive)
                    throw new InvalidOperationException(
                        $"PcCompat dynamic getter scheduled outside UnityMain: {operation}.");
                using var execution = PcCompatManagedExecutionContext.Enter(owner);
                result = callback();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                callbackLease.Dispose();
                Volatile.Write(ref workState, 3);
                completion.Set();
            }
        }

        bool queued;
        try
        {
            queued = scheduler(Run);
        }
        catch
        {
            if (Interlocked.CompareExchange(ref workState, 2, 0) == 0)
                callbackLease.Dispose();
            throw;
        }
        if (!queued)
        {
            if (Interlocked.CompareExchange(ref workState, 2, 0) == 0)
                callbackLease.Dispose();
            throw new InvalidOperationException(
                $"PcCompat dynamic getter UnityMain queue rejected: {operation}.");
        }
        var signaled = WaitHandle.WaitAny(
            [completion.WaitHandle, retirementToken.WaitHandle],
            timeoutMilliseconds);
        if (signaled == 1)
        {
            if (Interlocked.CompareExchange(ref workState, 2, 0) == 0)
                callbackLease.Dispose();
            throw new OperationCanceledException(
                $"PcCompat dynamic getter session retired while awaiting UnityMain: {operation}.",
                retirementToken);
        }
        if (signaled == WaitHandle.WaitTimeout)
        {
            if (Interlocked.CompareExchange(ref workState, 2, 0) == 0)
            {
                    callbackLease.Dispose();
                throw new TimeoutException(
                    $"PcCompat dynamic getter UnityMain wait exceeded " +
                    $"{timeoutMilliseconds} ms: {operation}.");
            }
            throw new TimeoutException(
                $"PcCompat dynamic getter UnityMain execution exceeded " +
                $"{timeoutMilliseconds} ms: {operation}.");
        }
        failure?.Throw();
        return result;
    }

    internal static void ReportManagedContinuationFailure(
        PcCompatManagedExecutionState? owner,
        string error)
    {
        if (owner == null)
        {
            Logger.Error(
                LogTag,
                "unowned managed UnityMain continuation was rejected: " + error);
            return;
        }

        PcCompatManagedModSession? session;
        lock (SessionLock)
            Sessions.TryGetValue(owner.ModId, out session);
        if (session == null ||
            session.ResourceSessionGeneration != owner.ResourceSessionGeneration)
            return;

        session.ReportManagedContinuationFailure(owner, error);
    }

    public static bool TryRequestManagedSelfRender(string modId, out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        PcCompatManagedModSession? session;
        lock (SessionLock)
            Sessions.TryGetValue(modId, out session);
        if (session == null)
        {
            error = "managed session is not loaded";
            return false;
        }
        if (!session.UsesRewrittenAssembly)
        {
            error = "managed session is not using the rewritten assembly";
            return false;
        }
        if (session.ActivationFailed)
        {
            error = session.ActivationStatus ?? "a previous activation attempt failed";
            return false;
        }
        if (session.Lifecycle.State == PcCompatManagedLifecycleState.Enabled ||
            session.ActivationPending)
        {
            error = null;
            return true;
        }
        if (session.Lifecycle.State != PcCompatManagedLifecycleState.Loaded)
        {
            error = $"managed lifecycle is {session.Lifecycle.State}";
            return false;
        }

        session.RequestActivation();
        PcCompatKeyViewerFallbackRuntime.Unregister(modId);
        UpdateManagedFrameGate();
        error = null;
        return true;
    }

    internal static void RegisterManagedFrameGateSink(
        Action<PcCompatManagedFrameDispatchMode>? sink)
    {
        Volatile.Write(ref s_managedFrameGateSink, sink);
        PcCompatKeyViewerPreviewRuntime.RegisterDemandChangedSink(
            sink == null ? null : UpdateManagedFrameGate);
        PcCompatKeyViewerFallbackRuntime.RegisterDemandChangedSink(
            sink == null ? null : UpdateManagedFrameGate);
        PcCompatKeyViewerLabelProjectionRuntime.RegisterDemandChangedSink(
            sink == null ? null : UpdateManagedFrameGate);
        PcCompatManagedComponentBridge.RegisterDemandChangedSink(
            sink == null ? null : UpdateManagedFrameGate);
        Interlocked.Exchange(ref s_managedFrameGateState, -1);
        UpdateManagedFrameGate();
    }

    internal static void RegisterManagedOnGUIGateSink(Action<bool>? sink)
    {
        Volatile.Write(ref s_managedOnGUIGateSink, sink);
        Interlocked.Exchange(ref s_managedOnGUIGateState, -1);
        UpdateManagedFrameGate();
    }

    internal static void RegisterManagedPresentationOwnershipSink(
        Func<string, bool, bool>? sink)
        => Volatile.Write(ref s_managedPresentationOwnershipSink, sink);

    internal static void RegisterNativeRuleBundleRetireSink(Func<string, bool>? sink)
        => Volatile.Write(ref s_nativeRuleBundleRetireSink, sink);

    internal static void RegisterManagedPrefixOrderPlanSink(
        PcCompatManagedPrefixOrderPlanHandler? sink)
        => Volatile.Write(ref s_managedPrefixOrderPlanSink, sink);

    internal static void RegisterManagedPostfixOrderPlanSink(
        PcCompatManagedPostfixOrderPlanHandler? sink)
        => Volatile.Write(ref s_managedPostfixOrderPlanSink, sink);

    internal static void PublishManagedPrefixOrderPlan(
        string modId,
        IReadOnlyList<PcCompatManagedPrefixOrderEntry> entries)
        => Volatile.Read(ref s_managedPrefixOrderPlanSink)?.Invoke(modId, entries);

    internal static void PublishManagedPostfixOrderPlan(
        string modId,
        IReadOnlyList<PcCompatManagedPostfixOrderEntry> entries)
        => Volatile.Read(ref s_managedPostfixOrderPlanSink)?.Invoke(modId, entries);

    private static PcCompatManagedEventDrainHandler? s_managedEventDrain;
    private static PcCompatManagedBoxedValueHandler? s_managedBoxedValueReader;

    internal static void RegisterManagedEventSinks(
        PcCompatManagedEventDrainHandler? drain,
        PcCompatManagedBoxedValueHandler? boxedValueReader)
    {
        Volatile.Write(ref s_managedEventDrain, drain);
        Volatile.Write(ref s_managedBoxedValueReader, boxedValueReader);
    }

    internal static PcCompatManagedEventDrainHandler? ManagedEventDrain
        => Volatile.Read(ref s_managedEventDrain);

    internal static PcCompatManagedBoxedValueHandler? ManagedBoxedValueReader
        => Volatile.Read(ref s_managedBoxedValueReader);

    public static int DispatchManagedPrefix(
        string modId,
        uint patchId,
        ref PcCompatManagedPrefixInvocationV2 invocation)
    {
        var sessions = Volatile.Read(ref s_managedDispatchSnapshot).PrefixSessions;
        return sessions.TryGetValue(modId, out var session)
            ? session.DispatchManagedPrefix(patchId, ref invocation)
            : -4;
    }

    public static void DispatchManagedFrame(float deltaTime)
    {
        if (Interlocked.Exchange(ref s_managedFrameDispatchActive, 1) != 0)
            return;

        Monitor.Enter(ManagedDispatchLifecycleLock);
        ++s_managedDispatchDepth;
        try
        {
            PcCompatKeyViewerPreviewRuntime.DispatchFrame();
            PcCompatKeyViewerLabelProjectionRuntime.DispatchFrame();
            PcCompatKeyViewerFallbackRuntime.DispatchFrame(deltaTime);
            var sessions = Volatile.Read(ref s_managedDispatchSnapshot).FrameSessions;
            var frameGateChanged = false;

            // Per-MOD native rings preserve local FIFO, while dispatch_sequence
            // restores the cross-MOD Harmony order chosen by HookBroker. Collect
            // before any MOD Update, sort once in a reusable buffer, then invoke.
            ManagedEventCollector.Reset();
            foreach (var session in sessions)
            {
                if (!session.ActivationPending && session.RequiresManagedFrameDispatch)
                    session.TryCollectManagedCallbacks(ManagedEventCollector);
            }
            var boxedValueReader = ManagedBoxedValueReader;
            ManagedEventCollector.DispatchAll(boxedValueReader);

            foreach (var session in sessions)
            {
                var requiredFrameBefore = session.RequiresFrameDispatch;
                session.TryDispatchSettingsFrame();
                if (!session.RequiresManagedFrameDispatch)
                {
                    frameGateChanged |= requiredFrameBefore != session.RequiresFrameDispatch;
                    continue;
                }
                var before = session.Lifecycle.State;
                var activationWasPending = session.ActivationPending;
                var callbacksDispatchedBeforeUpdate = !activationWasPending;
                if (session.TryDispatchUpdate(deltaTime))
                {
                    var activationAccepted = false;
                    if (activationWasPending && session.EnableCompleted)
                    {
                        if (SetManagedPresentationOwnership(
                                session,
                                managedOwnsPresentation: true))
                        {
                            Logger.Info(
                                LogTag,
                                $"mod={session.Manifest.Id} managed_self_render=enabled");
                            activationAccepted = true;
                        }
                        else
                        {
                            session.Disable();
                            Logger.Error(
                                LogTag,
                                $"mod={session.Manifest.Id} managed_self_render=disabled " +
                                "reason=recipe presentation ownership transfer failed");
                        }
                    }
                    if (activationAccepted)
                        session.NotifyActivationCompletedObservers();
                    if (!callbacksDispatchedBeforeUpdate)
                        session.TryDispatchManagedCallbacks();
                    frameGateChanged |= activationWasPending ||
                                        requiredFrameBefore != session.RequiresFrameDispatch;
                    continue;
                }
                var after = session.Lifecycle;
                if (activationWasPending && session.ActivationFailed)
                {
                    frameGateChanged = true;
                    Logger.Error(
                        LogTag,
                        $"mod={session.Manifest.Id} managed_self_render=activation_failed " +
                        $"error={ManagedErrorSummary(session.ActivationStatus)} " +
                        $"report={session.ManagedFailureReportPath}");
                    continue;
                }
                if (before != PcCompatManagedLifecycleState.Faulted &&
                    after.State == PcCompatManagedLifecycleState.Faulted)
                {
                    if (session.ManagedPresentationClaimed)
                        SetManagedPresentationOwnership(session, managedOwnsPresentation: false);
                    Logger.Error(
                        LogTag,
                        $"mod={session.Manifest.Id} managed_self_render=frame_fault " +
                        $"error={ManagedErrorSummary(after.LastError)} " +
                        $"report={session.ManagedFailureReportPath}");
                }
                frameGateChanged |= activationWasPending ||
                                    requiredFrameBefore != session.RequiresFrameDispatch;
            }

            if (frameGateChanged)
                UpdateManagedFrameGate();
        }
        finally
        {
            --s_managedDispatchDepth;
            Monitor.Exit(ManagedDispatchLifecycleLock);
            Volatile.Write(ref s_managedFrameDispatchActive, 0);
        }
    }

    public static void DispatchManagedOnGUI()
    {
        if (Interlocked.Exchange(ref s_managedOnGUIDispatchActive, 1) != 0)
            return;

        Monitor.Enter(ManagedDispatchLifecycleLock);
        ++s_managedDispatchDepth;
        try
        {
            var sessions = Volatile.Read(ref s_managedDispatchSnapshot).OnGuiSessions;

            var frameGateChanged = false;
            foreach (var session in sessions)
            {
                if (!session.RequiresOnGUIDispatch)
                    continue;
                var requiredFrameBefore = session.RequiresFrameDispatch;
                var requiredOnGUIBefore = session.RequiresOnGUIDispatch;
                if (!session.TryDispatchOnGUI())
                {
                    if (session.ManagedPresentationClaimed)
                        SetManagedPresentationOwnership(session, managedOwnsPresentation: false);
                    frameGateChanged = true;
                    Logger.Error(
                        LogTag,
                        $"mod={session.Manifest.Id} managed_self_render=ongui_fault " +
                        $"error={ManagedErrorSummary(session.Lifecycle.LastError)} " +
                        $"report={session.ManagedFailureReportPath}");
                }
                frameGateChanged |=
                    requiredFrameBefore != session.RequiresFrameDispatch ||
                    requiredOnGUIBefore != session.RequiresOnGUIDispatch;
            }

            if (frameGateChanged)
                UpdateManagedFrameGate();
        }
        finally
        {
            --s_managedDispatchDepth;
            Monitor.Exit(ManagedDispatchLifecycleLock);
            Volatile.Write(ref s_managedOnGUIDispatchActive, 0);
        }
    }

    private static string ManagedErrorSummary(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return "<empty>";

        var firstLine = error.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? error;
        var summary = string.Join(' ', firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return summary.Length <= 1200 ? summary : summary[..1197] + "...";
    }

    public static PcCompatRecipeCompileReport? GetRecipeReport(string modId)
    {
        lock (SessionLock)
            return RecipeReports.TryGetValue(modId, out var report) ? report : null;
    }

    public static IReadOnlyList<PcCompatRecipeCompileReport> SnapshotRecipeReports()
    {
        lock (SessionLock)
            return RecipeReports.Values.ToArray();
    }

    public static PcCompatRecipeBundleInfo? GetRecipeBundle(string modId)
    {
        lock (SessionLock)
            return RecipeBundles.TryGetValue(modId, out var info) ? info : null;
    }

    public static IReadOnlyList<PcCompatRecipeBundleInfo> SnapshotRecipeBundles()
    {
        lock (SessionLock)
            return RecipeBundles.Values.ToArray();
    }

    public static PcCompatStaticPatchScanReport? GetStaticScanReport(string modId)
    {
        lock (SessionLock)
            return StaticScanReports.TryGetValue(modId, out var report) ? report : null;
    }

    public static IReadOnlyList<PcCompatStaticPatchScanReport> SnapshotStaticScanReports()
    {
        lock (SessionLock)
            return StaticScanReports.Values.ToArray();
    }

    public static PcCompatCallbackTranslationReport? GetCallbackTranslationReport(string modId)
    {
        lock (SessionLock)
            return CallbackTranslationReports.TryGetValue(modId, out var report) ? report : null;
    }

    public static IReadOnlyList<PcCompatCallbackTranslationReport> SnapshotCallbackTranslationReports()
    {
        lock (SessionLock)
            return CallbackTranslationReports.Values.ToArray();
    }

    public static PcCompatManagedAssemblyBundleInfo? GetManagedAssemblyBundle(string modId)
    {
        lock (SessionLock)
            return ManagedAssemblyBundles.TryGetValue(modId, out var bundle) ? bundle : null;
    }

    public static IReadOnlyList<PcCompatManagedAssemblyBundleInfo> SnapshotManagedAssemblyBundles()
    {
        lock (SessionLock)
            return ManagedAssemblyBundles.Values.ToArray();
    }

    public static string? GetManagedAssemblyError(string modId)
    {
        lock (SessionLock)
            return ManagedAssemblyErrors.TryGetValue(modId, out var error) ? error : null;
    }

    public static PcCompatResourceCompileInfo? GetResourceCompileInfo(string modId)
    {
        lock (SessionLock)
            return ResourceCompileInfos.TryGetValue(modId, out var info) ? info : null;
    }

    public static string? GetResourceCompileError(string modId)
    {
        lock (SessionLock)
            return ResourceCompileErrors.TryGetValue(modId, out var error) ? error : null;
    }

    private static PcCompatRecipeBundleInfo PublishRecipeBundle(
        PcModManifest manifest,
        PcCompatRecipeCompileReport report)
    {
        // The compiled bundle is the native runtime's source of truth. Failure here
        // must abort registration instead of leaving a MOD marked loaded without hooks.
        var bundle = PcCompatRecipeBundleCache.Write(manifest, report);
        lock (SessionLock)
            RecipeBundles[manifest.Id] = bundle;

        try
        {
            var dir = Path.Combine(manifest.FolderPath, ".pccompat");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "recipe_report.json"), PcCompatRecipeReportJson.Serialize(report));
            File.WriteAllText(Path.Combine(dir, "compiled_bundle.txt"), bundle.BundleDirectory);
        }
        catch (Exception ex)
        {
            // MOD-local audit files are optional once the atomically published bundle exists.
            Logger.Warn(LogTag, $"failed to write MOD-local recipe audit for mod={manifest.Id}: {ex.Message}");
        }

        return bundle;
    }

    private static void TryPublishResourceSession(PcModManifest manifest, PcCompatRecipeBundleInfo? bundle)
    {
        try
        {
            // Prefer the atomically published compiled recipe; fall back to the
            // MOD-local import artifact written by ResourceRecipeTool/probe.
            var resourceRecipePath =
                !string.IsNullOrWhiteSpace(bundle?.ResourceRecipePath) &&
                File.Exists(bundle!.ResourceRecipePath)
                    ? bundle.ResourceRecipePath
                    : Path.Combine(manifest.FolderPath, ".pccompat", "resource_recipe.bin");
            if (!File.Exists(resourceRecipePath))
            {
                Logger.Info(
                    LogTag,
                    $"resource session skipped mod={manifest.Id} reason=resource_recipe.bin missing " +
                    $"hint=run ResourceRecipeTool compile or PcCompatProbe --recipe-only path={resourceRecipePath}");
                return;
            }

            var compiledResourcesDir =
                !string.IsNullOrWhiteSpace(bundle?.ResourceDirectory) &&
                Directory.Exists(bundle!.ResourceDirectory)
                    ? bundle.ResourceDirectory
                    : null;

            if (PcCompatResourceRecipeRuntime.TryLoadForMod(
                    manifest,
                    resourceRecipePath,
                    compiledResourcesDir))
            {
                var readiness = PcCompatResourceRecipeRuntime.GetReadinessSummary(manifest.Id);
                Logger.Info(
                    LogTag,
                    $"resource session mod={manifest.Id} compatibility={readiness.Compatibility} " +
                    $"groups={readiness.FeatureGroupCount} rawCandidateReady={readiness.ReadyCandidateCount}/" +
                    $"{readiness.CandidateCount} loadEnabled={readiness.RuntimeLoadEnabled}");
                TryPublishVirtualBundleSession(manifest, bundle, resourceRecipePath);
            }
            else
            {
                Logger.Warn(
                    LogTag,
                    $"resource session rejected mod={manifest.Id} path={resourceRecipePath}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"failed to publish resource session for mod={manifest.Id}: {ex.Message}");
        }
    }

    private static void TryPublishVirtualBundleSession(
        PcModManifest manifest,
        PcCompatRecipeBundleInfo? bundle,
        string resourceRecipePath)
    {
        var resourceIrPath =
            !string.IsNullOrWhiteSpace(bundle?.ResourceIrPath) && File.Exists(bundle.ResourceIrPath)
                ? bundle.ResourceIrPath
                : Path.Combine(manifest.FolderPath, ".pccompat", "resource_ir.bin");
        if (!File.Exists(resourceIrPath))
        {
            Logger.Info(
                LogTag,
                $"VirtualBundle session skipped mod={manifest.Id} reason=resource_ir.bin missing");
            return;
        }
        string? recipeError;
        string? irError = null;
        string? consistencyError = null;
        string? payloadError = null;
        if (!PcCompatResourceRecipe.TryRead(resourceRecipePath, out var recipe, out recipeError) ||
            !PcCompatResourceIr.TryRead(resourceIrPath, manifest.Id, out var resourceIr, out irError) ||
            !PcCompatResourceIr.TryValidateAgainstRecipe(resourceIr, recipe, out consistencyError) ||
            !PcCompatResourceIr.TryVerifyPayloadFiles(resourceIrPath, resourceIr, out payloadError))
        {
            throw new InvalidDataException(
                "VirtualBundle source validation failed: " +
                (recipeError ?? irError ?? consistencyError ?? payloadError ?? "unknown"));
        }
        if (!PcCompatResourceRecipeRuntime.TryGetSessionGeneration(manifest.Id, out var generation) ||
            generation <= 0)
            throw new InvalidOperationException("VirtualBundle resource session generation is unavailable.");

        PcCompatVirtualBundleRegistry.RegisterSession(
            manifest.Id,
            generation,
            manifest.FolderPath,
            resourceIrPath,
            resourceIr);
        PcCompatResourceChangerRuntime.TryRepublish(manifest.Id);
        var readiness = PcCompatVirtualBundleRegistry.GetSessionReadiness(manifest.Id, generation);
        Logger.Info(
            LogTag,
            $"VirtualBundle session mod={manifest.Id} generation={generation} " +
            $"bundles={resourceIr.Bundles.Count} assets={resourceIr.Assets.Count} " +
            $"requiredReady={readiness.RequiredReadyCount}/{readiness.RequiredAssetCount} " +
            $"requiredPending={readiness.RequiredPendingCount} " +
            $"requiredUnsupported={readiness.RequiredUnsupportedCount} " +
            $"requiredFailed={readiness.RequiredFailedCount}");
    }

    private static void WriteStaticScanReport(PcModManifest manifest, PcCompatStaticPatchScanReport report)
    {
        try
        {
            var dir = Path.Combine(manifest.FolderPath, ".pccompat");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "static_patch_scan.json"), report.ToJson());
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"failed to write static patch scan for mod={manifest.Id}: {ex.Message}");
        }
    }

    private static void WriteCallbackTranslationReport(
        PcModManifest manifest,
        PcCompatCallbackTranslationReport report)
    {
        try
        {
            var dir = Path.Combine(manifest.FolderPath, ".pccompat");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "callback_translation.json"), report.ToJson());
        }
        catch (Exception ex)
        {
            Logger.Warn(LogTag, $"failed to write callback translation report for mod={manifest.Id}: {ex.Message}");
        }
    }

    private static int GetTargetGameRevision()
    {
        var value = Environment.GetEnvironmentVariable("STARRAY_PCMOD_COMPAT_GAME_REVISION");
        return int.TryParse(value, out var revision) && revision > 0
            ? revision
            : PcCompatStaticPatchScanner.DefaultTargetGameRevision;
    }

    private static bool EnvEnabled(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var lineBreak = value.IndexOfAny(['\r', '\n']);
        return lineBreak < 0 ? value : value[..lineBreak];
    }

    private static void UpdateManagedFrameGate()
    {
        PcCompatManagedFrameDispatchMode mode;
        int onGuiSessionCount;
        lock (SessionLock)
        {
            var active = new List<PcCompatManagedModSession>(Sessions.Count);
            var onGui = new List<PcCompatManagedModSession>(Sessions.Count);
            var hasContinuous = false;
            var hasPending = false;
            foreach (var session in Sessions.Values)
            {
                if (session.RequiresFrameDispatch)
                    active.Add(session);
                if (session.RequiresOnGUIDispatch)
                    onGui.Add(session);
                hasContinuous |= session.RequiresContinuousFrameDispatch;
                hasPending |= session.ActivationPending;
            }
            var frameSessions = active.Count == 0
                ? Array.Empty<PcCompatManagedModSession>()
                : active.ToArray();
            var onGuiSessions = onGui.Count == 0
                ? Array.Empty<PcCompatManagedModSession>()
                : onGui.ToArray();
            var hasAdapterPump = PcCompatKeyViewerPreviewRuntime.HasPumpDemand ||
                                 PcCompatKeyViewerFallbackRuntime.HasDemand ||
                                 PcCompatKeyViewerLabelProjectionRuntime.HasDemand;
            mode = hasContinuous || hasAdapterPump
                ? PcCompatManagedFrameDispatchMode.Active
                : hasPending
                    ? PcCompatManagedFrameDispatchMode.PendingActivation
                    : PcCompatManagedFrameDispatchMode.Disabled;
            Volatile.Write(
                ref s_managedDispatchSnapshot,
                new ManagedDispatchSnapshot(
                    frameSessions,
                    onGuiSessions,
                    new Dictionary<string, PcCompatManagedModSession>(
                        Sessions,
                        StringComparer.OrdinalIgnoreCase)));
            onGuiSessionCount = onGuiSessions.Length;
        }

        var frameSink = Volatile.Read(ref s_managedFrameGateSink);
        var frameValue = (int)mode;
        if (frameSink != null &&
            Interlocked.Exchange(ref s_managedFrameGateState, frameValue) != frameValue)
        {
            try
            {
                frameSink(mode);
            }
            catch (Exception exception)
            {
                Logger.Error(LogTag, $"managed UnityMain frame gate failed mode={mode}: {exception}");
            }
        }

        var onGuiSink = Volatile.Read(ref s_managedOnGUIGateSink);
        var onGuiEnabled = onGuiSessionCount != 0;
        var onGuiValue = onGuiEnabled ? 1 : 0;
        if (onGuiSink != null &&
            Interlocked.Exchange(ref s_managedOnGUIGateState, onGuiValue) != onGuiValue)
        {
            try
            {
                onGuiSink(onGuiEnabled);
            }
            catch (Exception exception)
            {
                Logger.Error(
                    LogTag,
                    $"managed UnityMain OnGUI gate failed enabled={onGuiEnabled}: {exception}");
            }
        }
    }

    private static bool SetManagedPresentationOwnership(
        PcCompatManagedModSession session,
        bool managedOwnsPresentation)
    {
        if (session.ManagedPresentationClaimed == managedOwnsPresentation)
            return true;
        if (managedOwnsPresentation &&
            !IsPcModSessionActive(session.Manifest.Id))
        {
            Logger.Warn(
                LogTag,
                $"managed presentation ownership rejected by inactive PC MOD session mod={session.Manifest.Id}");
            return false;
        }

        var sink = Volatile.Read(ref s_managedPresentationOwnershipSink);
        if (sink == null)
            return false;
        try
        {
            if (!sink(session.Manifest.Id, managedOwnsPresentation))
                return false;
            if (managedOwnsPresentation)
                session.MarkManagedPresentationClaimed();
            else
                session.ClearManagedPresentationClaim();
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(
                LogTag,
                $"managed presentation ownership failed mod={session.Manifest.Id} " +
                $"managed={managedOwnsPresentation}: {exception}");
            return false;
        }
    }

    private static bool RetireNativeRuleBundle(string modId)
    {
        var sink = Volatile.Read(ref s_nativeRuleBundleRetireSink);
        if (sink == null)
            return true;
        try
        {
            if (!sink(modId))
            {
                Logger.Warn(LogTag, $"native rule bundle retirement failed mod={modId}");
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(LogTag, $"native rule bundle retirement threw mod={modId}: {exception}");
            return false;
        }
    }
}
