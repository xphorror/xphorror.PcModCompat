using System;
using Il2CppInterop.Common.Host;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.XrefScans;

namespace Il2CppInterop.Runtime.Startup;

public record RuntimeConfiguration
{
    public Version UnityVersion { get; init; }
    public IDetourProvider DetourProvider { get; init; }
    public bool EnableXrefScanner { get; init; } = true;
    public bool EnableClassInjection { get; init; } = true;
}

public sealed class Il2CppInteropRuntime : BaseHost
{
    private Il2CppInteropRuntime()
    {
    }

    public static Il2CppInteropRuntime Instance => GetInstance<Il2CppInteropRuntime>();

    public Version UnityVersion { get; private init; }

    public IDetourProvider DetourProvider { get; private init; }
    public bool ClassInjectionEnabled { get; private init; }

    public static Il2CppInteropRuntime Create(RuntimeConfiguration configuration)
    {
#if IL2CPPINTEROP_ANDROID_SLIM
        if (configuration.EnableXrefScanner)
            throw new NotSupportedException("Xref scanner cannot be enabled in the Android slim runtime.");
#endif
        var res = new Il2CppInteropRuntime
        {
            UnityVersion = configuration.UnityVersion,
            DetourProvider = configuration.DetourProvider,
            ClassInjectionEnabled = configuration.EnableClassInjection
        };
        SetInstance(res);
#if !IL2CPPINTEROP_ANDROID_SLIM
        if (configuration.EnableXrefScanner)
            res.AddXrefScanner<Il2CppInteropRuntime, XrefScanImpl>();
#endif
        return res;
    }

    public override void Start()
    {
        UnityVersionHandler.RecalculateHandlers();
        base.Start();
    }
}
