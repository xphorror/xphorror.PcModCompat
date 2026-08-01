using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.Attributes;
using String = Il2CppSystem.String;
using Void = Il2CppSystem.Void;

namespace Il2CppInterop.Runtime;

public static class Il2CppClassPointerStore
{
    public static IntPtr GetNativeClassPointer(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(void))
        {
            return IL2CPP.RequireIl2CppClass(
                Il2CppClassPointerStore<Void>.NativeClassPtr,
                type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
        }
        if (type == typeof(String))
        {
            return IL2CPP.RequireIl2CppClass(
                Il2CppClassPointerStore<string>.NativeClassPtr,
                type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
        }

        var closedStore = typeof(Il2CppClassPointerStore<>).MakeGenericType(type);
        var getter = closedStore.GetMethod(
            nameof(Il2CppClassPointerStore<int>.GetNativeClassPointerForGenericArgument),
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                closedStore.FullName,
                nameof(Il2CppClassPointerStore<int>.GetNativeClassPointerForGenericArgument));
        return getter.Invoke(null, null) is IntPtr pointer
            ? pointer
            : throw new InvalidOperationException(
                $"IL2CPP class pointer getter returned an invalid value for '{type.AssemblyQualifiedName}'.");
    }

    internal static void SetNativeClassPointer(Type type, IntPtr value)
    {
        ArgumentNullException.ThrowIfNull(type);
        var closedStore = typeof(Il2CppClassPointerStore<>).MakeGenericType(type);
        var field = closedStore.GetField(nameof(Il2CppClassPointerStore<int>.NativeClassPtr))
                    ?? throw new MissingFieldException(
                        closedStore.FullName,
                        nameof(Il2CppClassPointerStore<int>.NativeClassPtr));
        field.SetValue(null, value);
    }
}

public static class Il2CppClassPointerStore<T>
{
    public static IntPtr NativeClassPtr;
    public static Type CreatedTypeRedirect;

    static Il2CppClassPointerStore()
    {
        var targetType = typeof(T);
        if (!targetType.IsEnum)
        {
            RuntimeHelpers.RunClassConstructor(targetType.TypeHandle);
        }
        else
        {
            var assemblyName = targetType.Module.Name;
            var @namespace = targetType.Namespace ?? "";
            var name = targetType.Name;
            foreach (var customAttribute in targetType.CustomAttributes)
            {
                if (customAttribute.AttributeType != typeof(OriginalNameAttribute)) continue;
                assemblyName = (string)customAttribute.ConstructorArguments[0].Value;
                @namespace = (string)customAttribute.ConstructorArguments[1].Value;
                name = (string)customAttribute.ConstructorArguments[2].Value;
            }

            if (targetType.IsNested)
                NativeClassPtr =
                    IL2CPP.GetIl2CppNestedType(Il2CppClassPointerStore.GetNativeClassPointer(targetType.DeclaringType),
                        name);
            else
                NativeClassPtr =
                    IL2CPP.GetIl2CppClass(assemblyName, @namespace, name);
        }

        if (targetType.IsPrimitive || targetType == typeof(string))
            RuntimeHelpers.RunClassConstructor(AppDomain.CurrentDomain.GetAssemblies()
                .Single(it => it.GetName().Name == "Il2Cppmscorlib").GetType("Il2Cpp" + targetType.FullName)
                .TypeHandle);

        foreach (var customAttribute in targetType.CustomAttributes)
        {
            if (customAttribute.AttributeType != typeof(AlsoInitializeAttribute)) continue;

            var linkedType = (Type)customAttribute.ConstructorArguments[0].Value;
            RuntimeHelpers.RunClassConstructor(linkedType.TypeHandle);
        }
    }

    public static IntPtr GetNativeClassPointerForGenericArgument()
    {
        if (NativeClassPtr != IntPtr.Zero)
            return NativeClassPtr;

        var targetType = typeof(T);
        RuntimeHelpers.RunClassConstructor(targetType.TypeHandle);
        if (NativeClassPtr != IntPtr.Zero)
            return NativeClassPtr;

        if (!targetType.IsGenericType &&
            !targetType.IsArray &&
            !targetType.IsByRef &&
            !targetType.IsPointer)
        {
            var assemblyName = targetType.Module.Name;
            var @namespace = targetType.Namespace ?? "";
            var name = targetType.Name;
            foreach (var customAttribute in targetType.CustomAttributes)
            {
                if (customAttribute.AttributeType != typeof(OriginalNameAttribute)) continue;
                assemblyName = (string)customAttribute.ConstructorArguments[0].Value;
                @namespace = (string)customAttribute.ConstructorArguments[1].Value;
                name = (string)customAttribute.ConstructorArguments[2].Value;
            }

            NativeClassPtr = targetType.IsNested
                ? IL2CPP.GetIl2CppNestedType(
                    Il2CppClassPointerStore.GetNativeClassPointer(targetType.DeclaringType!),
                    name)
                : IL2CPP.GetIl2CppClass(assemblyName, @namespace, name);
        }

        if (NativeClassPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"IL2CPP class pointer is unavailable for generic argument '{targetType.AssemblyQualifiedName}'.");
        }

        return NativeClassPtr;
    }
}
