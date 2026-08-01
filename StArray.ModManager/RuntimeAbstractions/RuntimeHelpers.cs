namespace StArray.ModManager.RuntimeAbstractions;

public static class RuntimeHelpers
{
    public static void InstanceVoid(nint ptr, string methodName, int paramCount, nint[]? args)
        => new RuntimeObject(ptr).InvokeVoid(methodName, paramCount, args);

    public static nint InstanceRet(nint ptr, string methodName, int paramCount, nint[]? args)
        => new RuntimeObject(ptr).Invoke(methodName, paramCount, args);

    public static T InstanceRetUnbox<T>(
        nint ptr,
        string methodName,
        int paramCount,
        nint[]? args) where T : unmanaged
        => new RuntimeObject(ptr).InvokeUnbox<T>(methodName, paramCount, args);

    private static IRuntimeMethod? ResolveStaticMethod(
        string assemblyName,
        string ns,
        string className,
        string methodName,
        int paramCount)
        => RuntimeManager.GetDomain()
            ?.OpenAssembly(assemblyName)
            ?.GetClass(ns, className)
            ?.GetMethod(methodName, paramCount);

    public static void StaticVoid(
        string assemblyName,
        string ns,
        string className,
        string methodName,
        int paramCount,
        nint[]? args)
        => ResolveStaticMethod(assemblyName, ns, className, methodName, paramCount)
            ?.InvokeStatic(args);

    public static nint StaticRet(
        string assemblyName,
        string ns,
        string className,
        string methodName,
        int paramCount,
        nint[]? args)
        => ResolveStaticMethod(assemblyName, ns, className, methodName, paramCount)
            ?.InvokeStatic(args) ?? 0;

    public static T StaticRetUnbox<T>(
        string assemblyName,
        string ns,
        string className,
        string methodName,
        int paramCount,
        nint[]? args) where T : unmanaged
        => ResolveStaticMethod(assemblyName, ns, className, methodName, paramCount) is { } method
            ? method.InvokeStaticUnbox<T>(args)
            : default;

    public static T GetField<T>(nint ptr, string fieldName) where T : unmanaged
        => new RuntimeObject(ptr).GetField<T>(fieldName);

    public static void SetField<T>(nint ptr, string fieldName, T value) where T : unmanaged
        => new RuntimeObject(ptr).SetField(fieldName, value);

    private static IRuntimeField? ResolveStaticField(
        string assemblyName,
        string ns,
        string className,
        string fieldName)
        => RuntimeManager.GetDomain()
            ?.OpenAssembly(assemblyName)
            ?.GetClass(ns, className)
            ?.GetField(fieldName);

    public static T GetStaticField<T>(
        string assemblyName,
        string ns,
        string className,
        string fieldName) where T : unmanaged
        => ResolveStaticField(assemblyName, ns, className, fieldName) is { } field
            ? field.GetValue<T>(0)
            : default;

    public static void SetStaticField<T>(
        string assemblyName,
        string ns,
        string className,
        string fieldName,
        T value) where T : unmanaged
        => ResolveStaticField(assemblyName, ns, className, fieldName)?.SetValue(0, value);
}
