using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

/// <summary>
/// Materializes GUILayout options that exist in the PC API but whose public
/// GUILayout helper was stripped from the Android 3.1.2 metadata surface.
/// </summary>
internal static unsafe class PcCompatAndroidImGuiOptionBridge
{
    private const string ImGuiAssembly = "UnityEngine.IMGUIModule.dll";
    private const string OptionNamespace = "UnityEngine";
    private const string OptionName = "GUILayoutOption";
    private static readonly Lazy<NativeOptionContract> Contract = new(ResolveContract);

    [ModuleInitializer]
    internal static void Register()
        => PcCompatManagedImGuiBridge.RegisterNativeOptionFactory(CreateOption);

    internal static int ToNativeOptionType(PcCompatImGuiOptionKind kind)
        => kind switch
        {
            PcCompatImGuiOptionKind.MaxWidth => 3,
            PcCompatImGuiOptionKind.MinHeight => 4,
            PcCompatImGuiOptionKind.MaxHeight => 5,
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "The native GUILayout option materializer only owns stripped option kinds.")
        };

    private static IntPtr CreateOption(PcCompatImGuiOptionKind kind, float value)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "GUILayout option value must be finite.");

        var contract = Contract.Value;
        var option = IL2CPP.RequireIl2CppObject(
            IL2CPP.il2cpp_object_new(contract.OptionClass),
            "UnityEngine.GUILayoutOption allocation");
        var optionType = ToNativeOptionType(kind);
        var boxedValue = BoxSingle(contract.SingleClass, value);
        var parameters = stackalloc void*[2];
        parameters[0] = &optionType;
        // Reference parameters use a pointer-sized slot containing the Il2CppObject*.
        parameters[1] = (void*)boxedValue;

        var exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(
            contract.Constructor,
            option,
            parameters,
            ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return option;
    }

    private static IntPtr BoxSingle(IntPtr singleClass, float value)
    {
        var pointer = IL2CPP.il2cpp_value_box(singleClass, (IntPtr)(&value));
        return IL2CPP.RequireIl2CppObject(pointer, "System.Single GUILayout option value");
    }

    private static NativeOptionContract ResolveContract()
    {
        var optionClass = IL2CPP.RequireIl2CppClass(
            IL2CPP.GetIl2CppClass(ImGuiAssembly, OptionNamespace, OptionName),
            ImGuiAssembly + ":" + OptionNamespace + "." + OptionName);
        var enumClass = IL2CPP.RequireIl2CppClass(
            IL2CPP.GetIl2CppNestedType(optionClass, "Type"),
            "UnityEngine.GUILayoutOption.Type");
        var enumTypeName = IL2CPP.il2cpp_type_get_name_(
            IL2CPP.GetIl2CppTypeForClass(enumClass, "UnityEngine.GUILayoutOption.Type"))
            ?? throw new MissingMemberException("UnityEngine.GUILayoutOption.Type", "runtime type name");
        var constructor = IL2CPP.GetIl2CppMethodExact(
            optionClass,
            isGeneric: false,
            isStatic: false,
            genericArity: 0,
            ".ctor",
            "System.Void",
            enumTypeName,
            "System.Object");
        var singleClass = IL2CPP.RequireIl2CppClass(
            IL2CPP.GetIl2CppClass("mscorlib.dll", "System", "Single"),
            "mscorlib.dll:System.Single");
        return new NativeOptionContract(optionClass, constructor, singleClass);
    }

    private readonly record struct NativeOptionContract(
        IntPtr OptionClass,
        IntPtr Constructor,
        IntPtr SingleClass);
}
