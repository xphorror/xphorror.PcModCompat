using StArray.ModManager.Il2Cpp;

namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>IL2CPP managed object 的统一 Android 包装。</summary>
public readonly unsafe struct RuntimeObject
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeObject(nint ptr) => Ptr = ptr;

    private nint GetClassPtr()
        => RuntimeManager.IsIl2Cpp && Ptr != 0
            ? Il2CppFunctions.il2cpp_object_get_class(Ptr)
            : 0;

    public nint Invoke(string methodName, int paramCount, nint[]? args = null)
    {
        var klass = GetClassPtr();
        if (klass == 0)
            return 0;
        var method = Il2CppFunctions.il2cpp_class_get_method_from_name(klass, methodName, paramCount);
        return method != 0 ? new Il2CppMethod(method).Invoke(Ptr, args) : 0;
    }

    public nint Invoke(string methodName, nint[]? args = null)
        => Invoke(methodName, args?.Length ?? 0, args);

    public void InvokeVoid(string methodName, int paramCount = 0, nint[]? args = null)
        => Invoke(methodName, paramCount, args);

    public T InvokeUnbox<T>(
        string methodName,
        int paramCount = 0,
        nint[]? args = null) where T : unmanaged
    {
        var result = Invoke(methodName, paramCount, args);
        if (result == 0)
            return default;
        var unboxed = Il2CppRuntimeApi.Current.ObjectUnbox(result);
        return unboxed != 0 ? *(T*)unboxed : default;
    }

    public RuntimeObject? InvokeObject(
        string methodName,
        int paramCount = 0,
        nint[]? args = null)
    {
        var result = Invoke(methodName, paramCount, args);
        return result != 0 ? new RuntimeObject(result) : null;
    }

    public T GetField<T>(string fieldName) where T : unmanaged
    {
        var klass = GetClassPtr();
        if (klass == 0)
            return default;
        var field = Il2CppFunctions.il2cpp_class_get_field_from_name(klass, fieldName);
        return field != 0 ? new Il2CppField(field).GetValue<T>(Ptr) : default;
    }

    public void SetField<T>(string fieldName, T value) where T : unmanaged
    {
        var klass = GetClassPtr();
        if (klass == 0)
            return;
        var field = Il2CppFunctions.il2cpp_class_get_field_from_name(klass, fieldName);
        if (field != 0)
            new Il2CppField(field).SetValue(Ptr, value);
    }

    public T GetField<T>(IRuntimeField field) where T : unmanaged
        => field.GetValue<T>(Ptr);

    public void SetField<T>(IRuntimeField field, T value) where T : unmanaged
        => field.SetValue(Ptr, value);

    public static RuntimeObject? New(string assembly, string ns, string className)
    {
        var domain = RuntimeManager.GetDomain();
        return domain != null ? New(domain, assembly, ns, className) : null;
    }

    public static RuntimeObject? New(
        IAppDomain domain,
        string assembly,
        string ns,
        string className)
    {
        var klass = domain.OpenAssembly(assembly)?.GetClass(ns, className);
        if (klass == null)
            return null;
        var ptr = klass.New();
        return ptr != 0 ? new RuntimeObject(ptr) : null;
    }

    public override string ToString()
    {
        var obj = InvokeObject("ToString", 0);
        return obj.HasValue && obj.Value.Ptr != 0
            ? new RuntimeString(obj.Value.Ptr).ToString()
            : $"RuntimeObject(0x{Ptr:X})";
    }

    public nint this[string fieldName]
    {
        get => GetField<nint>(fieldName);
        set => SetField(fieldName, value);
    }

    public static implicit operator nint(RuntimeObject obj) => obj.Ptr;
    public static implicit operator RuntimeObject(nint ptr) => new(ptr);
}

/// <summary>把 runtime object pointer 包装为由 MOD定义的 stub 类型。</summary>
public readonly unsafe struct RuntimeObject<T> where T : UnmanagedObject
{
    public nint Ptr { get; }
    public bool IsValid => Ptr != 0;

    public RuntimeObject(nint ptr) => Ptr = ptr;
    public RuntimeObject(RuntimeObject obj) => Ptr = obj.Ptr;

    public T GetInstance()
    {
        if (Ptr == 0)
            throw new InvalidOperationException(
                $"Cannot create instance of {typeof(T).Name}: pointer is null.");
        if (IsStaticClass())
            throw new InvalidOperationException(
                $"Cannot create instance of {typeof(T).Name}: the underlying class is static.");
        return (T)Activator.CreateInstance(typeof(T), Ptr)!;
    }

    private bool IsStaticClass()
    {
        var klass = GetClassPtr();
        if (klass == 0)
            return false;
        const int abstractFlag = 0x80;
        const int sealedFlag = 0x100;
        var flags = Il2CppFunctions.il2cpp_class_get_flags(klass);
        return (flags & (abstractFlag | sealedFlag)) == (abstractFlag | sealedFlag);
    }

    private nint GetClassPtr()
        => RuntimeManager.IsIl2Cpp && Ptr != 0
            ? Il2CppFunctions.il2cpp_object_get_class(Ptr)
            : 0;

    public RuntimeObject AsRuntimeObject() => new(Ptr);
    public nint Invoke(string methodName, int paramCount, nint[]? args = null)
        => AsRuntimeObject().Invoke(methodName, paramCount, args);
    public nint Invoke(string methodName, nint[]? args = null)
        => AsRuntimeObject().Invoke(methodName, args);
    public void InvokeVoid(string methodName, int paramCount = 0, nint[]? args = null)
        => AsRuntimeObject().InvokeVoid(methodName, paramCount, args);
    public TRet InvokeUnbox<TRet>(
        string methodName,
        int paramCount = 0,
        nint[]? args = null) where TRet : unmanaged
        => AsRuntimeObject().InvokeUnbox<TRet>(methodName, paramCount, args);
    public RuntimeObject? InvokeObject(
        string methodName,
        int paramCount = 0,
        nint[]? args = null)
        => AsRuntimeObject().InvokeObject(methodName, paramCount, args);
    public TField GetField<TField>(string fieldName) where TField : unmanaged
        => AsRuntimeObject().GetField<TField>(fieldName);
    public void SetField<TField>(string fieldName, TField value) where TField : unmanaged
        => AsRuntimeObject().SetField(fieldName, value);
    public TField GetField<TField>(IRuntimeField field) where TField : unmanaged
        => AsRuntimeObject().GetField<TField>(field);
    public void SetField<TField>(IRuntimeField field, TField value) where TField : unmanaged
        => AsRuntimeObject().SetField(field, value);

    public nint this[string fieldName]
    {
        get => AsRuntimeObject()[fieldName];
        set
        {
            var obj = AsRuntimeObject();
            obj[fieldName] = value;
        }
    }

    public static implicit operator RuntimeObject(RuntimeObject<T> obj) => new(obj.Ptr);
    public static implicit operator RuntimeObject<T>(RuntimeObject obj) => new(obj);
    public static implicit operator RuntimeObject<T>(nint ptr) => new(ptr);
    public static implicit operator nint(RuntimeObject<T> obj) => obj.Ptr;
}
