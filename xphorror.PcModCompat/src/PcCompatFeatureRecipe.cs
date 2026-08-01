namespace Xphorror.PcModCompat;

[Flags]
public enum PcCompatCapability : ulong
{
    None = 0,
    ReadState = 1UL << 0,
    AfterOriginalObserve = 1UL << 1,
    Log = 1UL << 2,
    UiOverlay = 1UL << 3,
    ResourceRedirect = 1UL << 16,
    ReadIl2CppField = 1UL << 17,
    CallIl2CppGetter = 1UL << 18,
    WriteIl2CppField = 1UL << 32,
    CallIl2CppMutator = 1UL << 33,
    PatchReturn = 1UL << 34,
    SkipOriginal = 1UL << 35,
    ReplaceOriginal = 1UL << 36,
    InputInjection = 1UL << 37
}

public enum PcCompatFeatureStatus
{
    Supported,
    Partial,
    Unsupported,
    DisabledByCapability
}

public enum PcCompatRuleStage
{
    BeforeOriginal,
    AfterOriginal,
    ReplaceOriginal
}

public enum PcCompatRuleOp
{
    OverlayShow,
    OverlayShowPractice,
    OverlayHandleStateChange,
    OverlayHide,
    OverlayUpdatePlayers,
    PublishMarginSnapshot,
    ResourceRedirect,
    OverlayRecordHit,
    OverlayResetJudgement,
    OverlayRecordFloorMove,
    OverlayRecordPlayerHit,
    OverlayRecordDeath,
    OverlayRecordHitTiming,
    ResourceApplyEditorRabbit,
    ResourceApplyFloorColor,
    ResourceApplyPlanetColor,
    ResourceSkipPlanetColorOriginal,
    ResourceOverridePlanetColorArg,
    ResourceSkipTileColorOriginal,
    ResourceApplyLogoText,
    OverlayPollTelemetry,
    // Not a native domain effect: the hook only captures the raw instance/argument
    // slots and enqueues a per-MOD managed event. The MOD's own postfix callback is
    // invoked later on UnityMain by the managed callback dispatcher. Keep the numeric
    // value in sync with kRuleOpManagedEventCallback in pccompat_hook_rules.cpp.
    ManagedEventCallback = 21,
    // Native HookBroker observes scrPlayer.HitInputEvent after the original and
    // publishes only successful GameAction/Synthetic transitions.
    GameplayAcceptedObserve = 22,
    // Hook-thread synchronous reverse-P/Invoke. A void Prefix continues; a bool
    // Prefix returning false skips the original. Keep in sync with native.
    ManagedSynchronousPrefix = 23
}

public sealed class PcCompatCompiledRule
{
    public required string Id { get; init; }
    public required string FeatureId { get; init; }
    public string TargetAssemblyName { get; init; } = "Assembly-CSharp";
    public string TargetNamespace { get; init; } = string.Empty;
    public required string TargetType { get; init; }
    public required string TargetMethod { get; init; }
    public int? ParamCount { get; init; }
    public required bool TargetIsStatic { get; init; }
    public int TargetGenericArity { get; init; }
    public required string TargetReturnType { get; init; }
    public required IReadOnlyList<string> TargetParameterTypes { get; init; }
    public PcCompatRuleStage Stage { get; init; } = PcCompatRuleStage.AfterOriginal;
    public PcCompatRuleOp Op { get; init; }
    public PcCompatCapability RequiredCapabilities { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public string Source { get; init; } = "recipe";
}

public sealed class PcCompatCompiledFeature
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public PcCompatFeatureStatus Status { get; init; }
    public IReadOnlyList<string> RuleIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed class PcCompatUnsupportedItem
{
    public required string Id { get; init; }
    public required string Reason { get; init; }
    public string Severity { get; init; } = "info";
}

public sealed class PcCompatRecipeCompileReport
{
    public required string ModId { get; init; }
    public required string RecipeId { get; init; }
    public required string Compatibility { get; init; }
    public IReadOnlyList<PcCompatCompiledFeature> Features { get; init; } = Array.Empty<PcCompatCompiledFeature>();
    public IReadOnlyList<PcCompatCompiledRule> Rules { get; init; } = Array.Empty<PcCompatCompiledRule>();
    public IReadOnlyList<PcCompatUnsupportedItem> Unsupported { get; init; } = Array.Empty<PcCompatUnsupportedItem>();
    public PcCompatCapability RequiredCapabilities { get; init; }
    public IReadOnlyList<PcCompatUiObjectNode> UiObjectGraph { get; init; } =
        Array.Empty<PcCompatUiObjectNode>();
    public IReadOnlyList<PcCompatUiResourceBinding> UiResourceBindings { get; init; } =
        Array.Empty<PcCompatUiResourceBinding>();
    public IReadOnlyList<PcCompatUiLifecycleProgram> UiLifecyclePrograms { get; init; } = Array.Empty<PcCompatUiLifecycleProgram>();
}

[Flags]
public enum PcCompatUiComponentMask : uint
{
    None = 0,
    RectTransform = 1u << 0,
    Canvas = 1u << 1,
    CanvasScaler = 1u << 2,
    Image = 1u << 3,
    TextMeshProUGUI = 1u << 4,
    CanvasRenderer = 1u << 5,
    ContentSizeFitter = 1u << 6,
    RawImage = 1u << 7
}

[Flags]
public enum PcCompatUiObjectFlags : uint
{
    None = 0,
    ActiveInitially = 1u << 0,
    DontDestroyOnLoad = 1u << 1
}

public enum PcCompatUiComponentOpCode : uint
{
    SetActive = 1,
    SetRect = 2,
    SetAnchors = 3,
    SetPivot = 4,
    SetLocalScale = 5,
    SetCanvasRenderMode = 6,
    SetCanvasSortingOrder = 7,
    SetCanvasScaleMode = 8,
    SetCanvasReferenceResolution = 9,
    SetCanvasMatch = 10,
    SetGraphicColor = 11,
    SetGraphicRaycastTarget = 12,
    SetText = 13,
    SetTextFontSize = 14,
    SetTextAlignment = 15,
    SetTextRichText = 16,
    SetTextLineSpacing = 17,
    SetContentSizeHorizontalFit = 18,
    SetContentSizeVerticalFit = 19
}

public enum PcCompatPresentationCommandType : uint
{
    EnsureGraph = 1,
    SetActive = 2,
    SetRect = 3,
    SetText = 4,
    SetColor = 5,
    SetFontSize = 6,
    DestroyGraph = 7,
    InvalidateTarget = 8
}

public sealed class PcCompatUiComponentOperation
{
    public required PcCompatUiComponentOpCode OpCode { get; init; }
    public string StringValue { get; init; } = string.Empty;
    public long Payload0 { get; init; }
    public long Payload1 { get; init; }
    public long Payload2 { get; init; }
    public long Payload3 { get; init; }
}

public sealed class PcCompatUiObjectNode
{
    public required uint Id { get; init; }
    public uint ParentId { get; init; }
    public required string Name { get; init; }
    public PcCompatUiComponentMask Components { get; init; } =
        PcCompatUiComponentMask.RectTransform;
    public PcCompatUiObjectFlags Flags { get; init; }
    public IReadOnlyList<PcCompatUiComponentOperation> Initialization { get; init; } =
        Array.Empty<PcCompatUiComponentOperation>();
}

public enum PcCompatUiResourceTarget : uint
{
    ImageSprite = 1,
    RawImageTexture = 2,
    GraphicMaterial = 3,
    TextFont = 4,
    TextFontSharedMaterial = 5,
    TextFontMaterial = 6
}

public sealed class PcCompatUiResourceBinding
{
    public required uint NodeId { get; init; }
    public required PcCompatUiResourceTarget Target { get; init; }
    public required string FeatureGroupId { get; init; }
    public required string AssetName { get; init; }
    public required string ExpectedType { get; init; }
}

public enum PcCompatUiLifecycleTrigger : uint
{
    BundleLoad = 1,
    InputSnapshotChanged = 2,
    ClockAnchorChanged = 3,
    OverlayStateChanged = 4
}

public enum PcCompatUiClockDomain : uint
{
    Realtime = 0,
    UnityScaled = 1,
    Song = 2,
    Audio = 3,
    Map = 4
}

[Flags]
public enum PcCompatUiLifecycleFlags : uint
{
    None = 0,
    AllowAnchorExtrapolation = 1u << 0,
    RequireInputSnapshot = 1u << 1,
    RequireClockAnchor = 1u << 2
}

public enum PcCompatNativeVmOpcode : byte
{
    Nop = 0,
    LoadConstI64,
    LoadConstF64,
    MoveI64,
    MoveF64,
    AddI64,
    SubI64,
    MulI64,
    DivI64,
    AddF64,
    SubF64,
    MulF64,
    DivF64,
    CompareEqualI64,
    CompareLessI64,
    CompareEqualF64,
    CompareLessF64,
    NotPredicate,
    AndPredicate,
    OrPredicate,
    Branch,
    BranchIf,
    LoadRealtimeNs,
    LoadInputTotal,
    LoadInputKps,
    LoadInputHeldMask,
    LoadTouchLaneHeldMask,
    LoadTouchLaneHeldCount,
    LoadTouchLaneTotalCount,
    LoadUnityScaledTime,
    LoadUnityTimeScale,
    LoadUnityFrameCount,
    LoadSongPosition,
    LoadAudioPosition,
    LoadMapPosition,
    Return,
    LoadOverlayVisible
}

public readonly record struct PcCompatNativeVmInstruction(
    PcCompatNativeVmOpcode Opcode,
    byte Destination = 0,
    byte Source0 = 0,
    byte Source1 = 0,
    int Immediate = 0,
    long Payload = 0);

public sealed class PcCompatUiLifecycleProgram
{
    public required string Id { get; init; }
    public required uint RuntimeRuleId { get; init; }
    public PcCompatUiLifecycleTrigger Trigger { get; init; } = PcCompatUiLifecycleTrigger.BundleLoad;
    public PcCompatUiClockDomain ClockDomain { get; init; } = PcCompatUiClockDomain.Realtime;
    public PcCompatUiLifecycleFlags Flags { get; init; }
    public uint InstructionBudget { get; init; } = 1024;
    public required uint CommandType { get; init; }
    public uint TargetId { get; init; }
    public long InitialDelayNs { get; init; }
    public long DeferredRetryDelayNs { get; init; } = 5_000_000;
    public IReadOnlyList<PcCompatNativeVmInstruction> Instructions { get; init; } = Array.Empty<PcCompatNativeVmInstruction>();
}
