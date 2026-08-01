using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

/// <summary>
/// Java 类封装 — 持有一个 jclass 全局引用，提供方法/字段查找和静态方法调用
/// </summary>
public sealed class JavaClass : IDisposable
{
    public readonly IntPtr Handle; // jclass global ref
    private bool _disposed;

    /// <summary>通过 ClassLoader.loadClass 查找类</summary>
    public JavaClass(string className)
    {
        var directRef = JniHelperNative.FindClass(ToJniClassName(className));
        if (directRef != IntPtr.Zero)
        {
            Handle = directRef;
            return;
        }

        var localRef = FindViaClassLoader(ToBinaryClassName(className));
        if (localRef == IntPtr.Zero)
            throw new Exception($"JavaClass: '{className}' not found");
        Handle = JniHelperNative.NewGlobalRef(localRef);
        JniHelperNative.DeleteLocalRef(localRef);
    }

    /// <summary>包装已有的 jclass（自动转全局引用）</summary>
    public JavaClass(IntPtr clazz)
    {
        Handle = JniHelperNative.NewGlobalRef(clazz);
    }


    public IntPtr GetMethodID(string name, string sig)
        => JniHelperNative.GetMethodID(Handle, name, sig);

    public IntPtr GetStaticMethodID(string name, string sig)
        => JniHelperNative.GetStaticMethodID(Handle, name, sig);


    public IntPtr GetFieldID(string name, string sig)
        => JniHelperNative.GetFieldID(Handle, name, sig);

    public IntPtr GetStaticFieldID(string name, string sig)
        => JniHelperNative.GetStaticFieldID(Handle, name, sig);

    public IntPtr GetStaticObjectField(IntPtr fieldID)
        => JniHelperNative.GetStaticObjectField(Handle, fieldID);


    public unsafe IntPtr CallStaticObjectMethod0(nint m)
        => CallStaticObjectMethod(m, null);

    public unsafe IntPtr CallStaticObjectMethod1(nint m, nint a1)
    {
        var args = stackalloc JValue[1];
        args[0].L = a1;
        return CallStaticObjectMethod(m, args);
    }

    public unsafe IntPtr CallStaticObjectMethod2(nint m, nint a1, nint a2)
    {
        var args = stackalloc JValue[2];
        args[0].L = a1;
        args[1].L = a2;
        return CallStaticObjectMethod(m, args);
    }

    public unsafe IntPtr CallStaticObjectMethod3(nint m, nint a1, nint a2, int a3)
    {
        var args = stackalloc JValue[3];
        args[0].L = a1;
        args[1].L = a2;
        args[2].I = a3;
        return CallStaticObjectMethod(m, args);
    }

    /// <summary>Upstream overload for a third JNI reference argument.</summary>
    public unsafe IntPtr CallStaticObjectMethod3(nint m, nint a1, nint a2, nint a3)
    {
        var args = stackalloc JValue[3];
        args[0].L = a1;
        args[1].L = a2;
        args[2].L = a3;
        return CallStaticObjectMethod(m, args);
    }

    public unsafe void CallStaticVoidMethod0(nint m)
        => CallStaticVoidMethod(m, null);

    public unsafe void CallStaticVoidMethod1(nint m, bool a1)
    {
        var args = stackalloc JValue[1];
        args[0].Z = a1 ? (byte)1 : (byte)0;
        CallStaticVoidMethod(m, args);
    }

    /// <summary>Upstream overload for one JNI reference argument.</summary>
    public unsafe void CallStaticVoidMethod1(nint m, nint a1)
    {
        var args = stackalloc JValue[1];
        args[0].L = a1;
        CallStaticVoidMethod(m, args);
    }

    public unsafe void CallStaticVoidMethod2(nint m, nint a1, nint a2)
    {
        var args = stackalloc JValue[2];
        args[0].L = a1;
        args[1].L = a2;
        CallStaticVoidMethod(m, args);
    }

    public unsafe int CallStaticIntMethod0(nint m)
        => JniHelperNative.CallStaticIntMethodA(Handle, m, null);

    /// <summary>创建 Java String</summary>
    public nint NewString(string s) => JniHelperNative.NewString(s);

    public void Dispose()
    {
        if (!_disposed) { JniHelperNative.DeleteGlobalRef(Handle); _disposed = true; }
    }


    private static string ToJniClassName(string name) => name.Replace('.', '/');

    private static string ToBinaryClassName(string name) => name.Replace('/', '.');


    private static unsafe IntPtr FindViaClassLoader(string name)
    {
        var atClass = JniHelperNative.FindClass("android/app/ActivityThread");
        if (atClass == IntPtr.Zero) return IntPtr.Zero;
        var curAt = JniHelperNative.GetStaticMethodID(atClass, "currentActivityThread", "()Landroid/app/ActivityThread;");
        if (curAt == IntPtr.Zero) { JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var at = JniHelperNative.CallStaticObjectMethod(atClass, curAt);
        if (at == IntPtr.Zero) { JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var getApp = JniHelperNative.GetMethodID(atClass, "getApplication", "()Landroid/app/Application;");
        if (getApp == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var app = JniHelperNative.CallObjectMethodA(at, getApp, null);
        if (app == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var appCls = JniHelperNative.GetObjectClass(app);
        if (appCls == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(app); JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var getCl = JniHelperNative.GetMethodID(appCls, "getClassLoader", "()Ljava/lang/ClassLoader;");
        if (getCl == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(appCls); JniHelperNative.DeleteLocalRef(app); JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var cl = JniHelperNative.CallObjectMethodA(app, getCl, null);
        if (cl == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(appCls); JniHelperNative.DeleteLocalRef(app); JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var clCls = JniHelperNative.GetObjectClass(cl);
        if (clCls == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(cl); JniHelperNative.DeleteLocalRef(appCls); JniHelperNative.DeleteLocalRef(app); JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var loadClass = JniHelperNative.GetMethodID(clCls, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;");
        if (loadClass == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(clCls); JniHelperNative.DeleteLocalRef(cl); JniHelperNative.DeleteLocalRef(appCls); JniHelperNative.DeleteLocalRef(app); JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var jName = JniHelperNative.NewString(name);
        if (jName == IntPtr.Zero) { JniHelperNative.DeleteLocalRef(clCls); JniHelperNative.DeleteLocalRef(cl); JniHelperNative.DeleteLocalRef(appCls); JniHelperNative.DeleteLocalRef(app); JniHelperNative.DeleteLocalRef(at); JniHelperNative.DeleteGlobalRef(atClass); return IntPtr.Zero; }
        var args = stackalloc JValue[1];
        args[0].L = jName;
        var result = JniHelperNative.CallObjectMethodA(cl, loadClass, args);
        JniHelperNative.DeleteLocalRef(jName);
        JniHelperNative.DeleteLocalRef(clCls);
        JniHelperNative.DeleteLocalRef(cl);
        JniHelperNative.DeleteLocalRef(appCls);
        JniHelperNative.DeleteLocalRef(app);
        JniHelperNative.DeleteLocalRef(at);
        JniHelperNative.DeleteGlobalRef(atClass);
        return result;
    }


    private unsafe IntPtr CallStaticObjectMethod(nint method, JValue* args)
        => JniHelperNative.CallStaticObjectMethodA(Handle, method, args);

    private unsafe void CallStaticVoidMethod(nint method, JValue* args)
        => JniHelperNative.CallStaticVoidMethodA(Handle, method, args);
}

/// <summary>
/// Java 对象封装 — 持有一个 jobject 本地引用，提供实例方法调用和字段读取
/// </summary>
public sealed class JavaObject : IDisposable
{
    public readonly IntPtr Handle;
    private bool _disposed;

    public JavaObject(IntPtr obj) => Handle = obj;


    public unsafe IntPtr CallObjectMethod0(IntPtr m)
        => CallObjectMethod(m, null);

    public unsafe IntPtr CallObjectMethod1(IntPtr m, IntPtr a1)
    {
        var args = stackalloc JValue[1];
        args[0].L = a1;
        return CallObjectMethod(m, args);
    }

    public unsafe IntPtr CallObjectMethod2(IntPtr m, IntPtr a1, IntPtr a2)
    {
        var args = stackalloc JValue[2];
        args[0].L = a1;
        args[1].L = a2;
        return CallObjectMethod(m, args);
    }

    public unsafe void CallVoidMethod0(IntPtr m)
        => CallVoidMethod(m, null);

    public unsafe void CallVoidMethod1(IntPtr m, IntPtr a1)
    {
        var args = stackalloc JValue[1];
        args[0].L = a1;
        CallVoidMethod(m, args);
    }

    public unsafe void CallVoidMethod2(IntPtr m, IntPtr a1, IntPtr a2)
    {
        var args = stackalloc JValue[2];
        args[0].L = a1;
        args[1].L = a2;
        CallVoidMethod(m, args);
    }

    public unsafe bool CallBoolMethod2(IntPtr m, IntPtr a1, IntPtr a2)
    {
        var args = stackalloc JValue[2];
        args[0].L = a1;
        args[1].L = a2;
        return JniHelperNative.CallBooleanMethodA(Handle, m, args);
    }


    public IntPtr GetObjectField(IntPtr fieldID)
        => JniHelperNative.GetObjectField(Handle, fieldID);


    public JavaClass GetClass() => new(JniHelperNative.GetObjectClass(Handle));

    public void Dispose()
    {
        if (!_disposed) { JniHelperNative.DeleteLocalRef(Handle); _disposed = true; }
    }


    private unsafe IntPtr CallObjectMethod(IntPtr method, JValue* args)
        => JniHelperNative.CallObjectMethodA(Handle, method, args);

    private unsafe void CallVoidMethod(IntPtr method, JValue* args)
        => JniHelperNative.CallVoidMethodA(Handle, method, args);
}
public static class NativeFunctions {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnAcceptChar(uint codepoint);

    [DllImport("starray_modmanager", EntryPoint = "modmanager_set_OnAcceptCharCallback")]
    public static extern void SetOnAcceptCharCallback(OnAcceptChar onAcceptChar);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnAcceptKey(int keyCode);

    [DllImport("starray_modmanager", EntryPoint = "modmanager_set_OnAcceptKeyCallback")]
    public static extern void SetOnAcceptKeyCallback(OnAcceptKey onAcceptKey);
}
