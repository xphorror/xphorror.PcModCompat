using System.Collections.ObjectModel;

namespace StArray.ModManager.Interop;

/// <summary>
/// 跨 MOD 合同的可见范围。未指定时由 Broker 使用 Public。
/// </summary>
public enum InteropVisibility : byte
{
    Public = 0,
    DependenciesOnly = 1,
    AllowList = 2,
    Private = 3
}

/// <summary>消息投递策略。</summary>
public enum InteropDeliveryMode : byte
{
    OrderedLossless = 0,
    LatestState = 1,
    BestEffort = 2
}

/// <summary>受控回调上下文。</summary>
public enum InteropDispatchContext : byte
{
    SerializedWorker = 0,
    UnityMainBatched = 1
}

/// <summary>请求选择 Provider 的策略。</summary>
public enum InteropProviderSelection : byte
{
    Single = 0,
    FanOut = 1,
    Targeted = 2
}

public enum InteropErrorCode : ushort
{
    None = 0,
    InvalidArgument = 1,
    RuntimeUnavailable = 2,
    ContractNotFound = 3,
    ContractAlreadyRegistered = 4,
    VersionMismatch = 5,
    VisibilityDenied = 6,
    QuotaExceeded = 7,
    PublisherRetired = 8,
    SubscriberRetired = 9,
    GenerationMismatch = 10,
    SessionAlreadyActive = 11,
    SessionNotActive = 12,
    QueueOverflow = 13,
    CircuitBroken = 14,
    RequestLimitExceeded = 15,
    RequestTimeout = 16,
    Cancelled = 17,
    ProviderNotFound = 18,
    ProviderRetired = 19,
    HandlerUnavailable = 20,
    InternalFailure = 21
}

public readonly record struct InteropError(
    InteropErrorCode Code,
    string Message)
{
    public static InteropError None => new(InteropErrorCode.None, string.Empty);

    public bool IsError => Code != InteropErrorCode.None;

    public override string ToString()
        => IsError ? $"{Code}: {Message}" : "None";
}

/// <summary>
/// MOD 提交给 Broker 的合同声明。ContractId 可以是本地主题名；本地名会被扩展为
/// <c>mod/{publisherId}/{topic}</c>。只有 Broker 预注册的 starray.* 全局合同允许直接使用。
/// </summary>
public sealed class InteropContractDeclaration
{
    public InteropContractDeclaration(
        string contractId,
        int majorVersion = 1,
        int minorVersion = 0,
        string schemaId = "default",
        string contentType = "application/octet-stream")
    {
        ContractId = contractId ?? throw new ArgumentNullException(nameof(contractId));
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        SchemaId = schemaId ?? throw new ArgumentNullException(nameof(schemaId));
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
    }

    public string ContractId { get; }
    public int MajorVersion { get; }
    public int MinorVersion { get; }
    public string SchemaId { get; }
    public string ContentType { get; }
    public InteropVisibility Visibility { get; init; } = InteropVisibility.Public;
    public InteropDeliveryMode DeliveryMode { get; init; } = InteropDeliveryMode.OrderedLossless;
    public InteropDispatchContext DispatchContext { get; init; } = InteropDispatchContext.SerializedWorker;
    public int MaxPayloadBytes { get; init; } = 32 * 1024;
    public IReadOnlyList<string> AllowList { get; init; } = Array.Empty<string>();
}

public sealed class InteropSubscriptionRequest
{
    public InteropSubscriptionRequest(string contractId, int majorVersion = 1)
    {
        ContractId = contractId ?? throw new ArgumentNullException(nameof(contractId));
        MajorVersion = majorVersion;
    }

    public string ContractId { get; }
    public int MajorVersion { get; }
    public int MinimumMinorVersion { get; init; }
    public int MaximumMinorVersion { get; init; } = int.MaxValue;
    public string? TargetPublisherId { get; init; }
    public long? TargetPublisherGeneration { get; init; }
    public int QueueCapacity { get; init; } = 128;
    public InteropDispatchContext DispatchContext { get; init; } = InteropDispatchContext.SerializedWorker;
}

public sealed class InteropContractDescriptor
{
    internal InteropContractDescriptor(
        string contractId,
        int majorVersion,
        int minorVersion,
        string schemaId,
        string contentType,
        InteropVisibility visibility,
        InteropDeliveryMode deliveryMode,
        InteropDispatchContext dispatchContext,
        string publisherId,
        long publisherGeneration,
        bool hasRequestHandler)
    {
        ContractId = contractId;
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        SchemaId = schemaId;
        ContentType = contentType;
        Visibility = visibility;
        DeliveryMode = deliveryMode;
        DispatchContext = dispatchContext;
        PublisherId = publisherId;
        PublisherGeneration = publisherGeneration;
        HasRequestHandler = hasRequestHandler;
    }

    public string ContractId { get; }
    public int MajorVersion { get; }
    public int MinorVersion { get; }
    public string SchemaId { get; }
    public string ContentType { get; }
    public InteropVisibility Visibility { get; }
    public InteropDeliveryMode DeliveryMode { get; }
    public InteropDispatchContext DispatchContext { get; }
    public string PublisherId { get; }
    public long PublisherGeneration { get; }
    public bool HasRequestHandler { get; }
}

public enum VirtualInputDevice : byte
{
    Keyboard = 1,
    Touch = 2
}

public enum VirtualInputPhase : byte
{
    Down = 1,
    Move = 2,
    Up = 3,
    Cancel = 4
}

/// <summary>
/// V2 虚拟输入事件。Sequence 由 Broker 分配，发布者传入的值会被忽略。
/// </summary>
public readonly record struct VirtualInputEvent(
    ulong Sequence,
    long OffsetMicroseconds,
    VirtualInputDevice Device,
    VirtualInputPhase Phase,
    string? CanonicalKey,
    int PointerId,
    int RepeatCount,
    float X,
    float Y,
    float ViewportWidth,
    float ViewportHeight);

public enum VirtualInputBatchKind : byte
{
    Started = 1,
    Events = 2,
    Snapshot = 3,
    Cancelled = 4,
    Ended = 5
}

/// <summary>虚拟输入批次。事件数组在进入 Broker 后视为不可变。</summary>
public sealed class VirtualInputBatch
{
    private readonly VirtualInputEvent[] _events;

    public VirtualInputBatch(
        VirtualInputBatchKind kind,
        long sessionGeneration,
        IEnumerable<VirtualInputEvent>? events = null)
    {
        if (sessionGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionGeneration));
        Kind = kind;
        SessionGeneration = sessionGeneration;
        _events = events?.ToArray() ?? Array.Empty<VirtualInputEvent>();
    }

    public VirtualInputBatchKind Kind { get; }
    public long SessionGeneration { get; }
    public IReadOnlyList<VirtualInputEvent> Events => _events;
    public bool IsTerminal => Kind is VirtualInputBatchKind.Cancelled or VirtualInputBatchKind.Ended;
}

public sealed class InteropMessage
{
    private readonly byte[] _payload;

    internal InteropMessage(
        InteropContractDescriptor contract,
        string publisherId,
        long publisherGeneration,
        ulong sequence,
        byte[] payload,
        VirtualInputBatch? virtualInput,
        bool isCancellation)
    {
        Contract = contract;
        PublisherId = publisherId;
        PublisherGeneration = publisherGeneration;
        Sequence = sequence;
        _payload = payload;
        VirtualInput = virtualInput;
        IsCancellation = isCancellation;
    }

    public InteropContractDescriptor Contract { get; }
    public string PublisherId { get; }
    public long PublisherGeneration { get; }
    public ulong Sequence { get; }
    public ReadOnlyMemory<byte> Payload => _payload;
    public VirtualInputBatch? VirtualInput { get; }
    public bool IsCancellation { get; }
}

public sealed class InteropRequest
{
    private readonly byte[] _payload;

    public InteropRequest(
        string contractId,
        int majorVersion,
        ReadOnlySpan<byte> payload,
        TimeSpan timeout,
        InteropProviderSelection selection = InteropProviderSelection.Single)
    {
        CorrelationId = Guid.NewGuid();
        ContractId = contractId ?? throw new ArgumentNullException(nameof(contractId));
        MajorVersion = majorVersion;
        _payload = payload.ToArray();
        Timeout = timeout;
        Selection = selection;
    }

    public Guid CorrelationId { get; init; }
    public string ContractId { get; }
    public int MajorVersion { get; }
    public ReadOnlyMemory<byte> Payload => _payload;
    public TimeSpan Timeout { get; }
    public InteropProviderSelection Selection { get; }
    public string? TargetPublisherId { get; init; }
    public long? TargetPublisherGeneration { get; init; }
    public CancellationToken CancellationToken { get; internal init; }
}

public sealed class InteropResponse
{
    private readonly byte[] _payload;

    public InteropResponse(
        InteropErrorCode code,
        ReadOnlySpan<byte> payload = default,
        string? message = null)
    {
        Code = code;
        _payload = payload.ToArray();
        Message = message ?? string.Empty;
    }

    public InteropErrorCode Code { get; }
    public string Message { get; }
    public ReadOnlyMemory<byte> Payload => _payload;
    public bool Succeeded => Code == InteropErrorCode.None;

    public static InteropResponse Success(ReadOnlySpan<byte> payload = default)
        => new(InteropErrorCode.None, payload);

    public static InteropResponse Failure(InteropErrorCode code, string message)
        => new(code, message: message);
}

public sealed class InteropFanOutResponse
{
    internal InteropFanOutResponse(IReadOnlyList<InteropResponse> responses)
        => Responses = new ReadOnlyCollection<InteropResponse>(responses.ToArray());

    public IReadOnlyList<InteropResponse> Responses { get; }
}

public delegate void InteropMessageHandler(InteropMessage message);
public delegate ValueTask<InteropResponse> InteropRequestHandler(InteropRequest request);

public static class ModInteropConstants
{
    public const string VirtualInputPlaybackV2 = "starray.virtual-input.playback.v2";
    public const string RequestResponseV1 = "starray.mod.interop.request-response.v1";
    public const int VirtualInputMaxBatch = 512;
    public const int VirtualInputQueueCapacity = 8192;
    public const int MaxCustomPayloadBytes = 32 * 1024;
}
