using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

/// <summary>
/// JNI Helper Native 函数绑定
/// 调用单一 native SO 中的 jnihelper C 函数
/// </summary>
public static class JniHelperNative
{
    private const string LibModManager = "starray_modmanager";
    
    /// <summary>
    /// 获取 Unity Surface（C 实现，更快速）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_unity_surface")]
    public static extern IntPtr GetUnitySurface();
    
    /// <summary>
    /// 获取 Unity ANativeWindow（从 Surface 转换，可用于 ImGui）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_unity_native_window")]
    public static extern IntPtr GetUnityNativeWindow();
    
    /// <summary>
    /// 获取当前 Activity 或 Application
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_current_activity")]
    public static extern IntPtr GetCurrentActivity();
    
    /// <summary>
    /// 获取 JavaVM
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_jvm")]
    public static extern IntPtr GetJavaVM();
    
    /// <summary>
    /// 获取 JNIEnv（自动附加当前线程）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_env")]
    public static extern IntPtr GetJNIEnv();
    
    /// <summary>
    /// 查找 Java 类（返回全局引用）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_find_class")]
    public static extern IntPtr FindClass([MarshalAs(UnmanagedType.LPStr)] string className);
    
    /// <summary>
    /// 获取方法 ID
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_method_id")]
    public static extern IntPtr GetMethodID(IntPtr clazz, 
        [MarshalAs(UnmanagedType.LPStr)] string methodName,
        [MarshalAs(UnmanagedType.LPStr)] string signature);
    
    /// <summary>
    /// 获取静态方法 ID
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_method_id")]
    public static extern IntPtr GetStaticMethodID(IntPtr clazz,
        [MarshalAs(UnmanagedType.LPStr)] string methodName,
        [MarshalAs(UnmanagedType.LPStr)] string signature);
    
    /// <summary>
    /// 获取字段 ID
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_field_id")]
    public static extern IntPtr GetFieldID(IntPtr clazz,
        [MarshalAs(UnmanagedType.LPStr)] string fieldName,
        [MarshalAs(UnmanagedType.LPStr)] string signature);
    
    /// <summary>
    /// 获取静态字段 ID
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_field_id")]
    public static extern IntPtr GetStaticFieldID(IntPtr clazz,
        [MarshalAs(UnmanagedType.LPStr)] string fieldName,
        [MarshalAs(UnmanagedType.LPStr)] string signature);
    
    /// <summary>
    /// 创建 Java String
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_new_string")]
    public static extern IntPtr NewString([MarshalAs(UnmanagedType.LPStr)] string str);

    [DllImport(LibModManager, EntryPoint = "jnihelper_new_string_utf")]
    public static extern IntPtr NewStringUtf(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string utf);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_string_utf_chars")]
    public static extern IntPtr GetStringUtfChars(IntPtr jstr);

    [DllImport(LibModManager, EntryPoint = "jnihelper_release_string_utf_chars")]
    public static extern void ReleaseStringUtfChars(IntPtr jstr, IntPtr utf);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_string_length")]
    public static extern int GetStringLength(IntPtr jstr);
    
    /// <summary>
    /// Java String 转 C 字符串（需要调用者释放内存）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_string")]
    private static extern IntPtr GetStringInternal(IntPtr jstr);
    
    /// <summary>
    /// Java String 转 C# string（自动管理内存）
    /// </summary>
    public static string? GetString(IntPtr jstr)
    {
        if (jstr == IntPtr.Zero)
            return null;
        
        IntPtr ptr = GetStringInternal(jstr);
        if (ptr == IntPtr.Zero)
            return null;
        
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            // 释放 C 分配的内存
            Marshal.FreeHGlobal(ptr);
        }
    }
    
    /// <summary>
    /// 删除本地引用
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_delete_local_ref")]
    public static extern void DeleteLocalRef(IntPtr obj);
    
    /// <summary>
    /// 删除全局引用
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_delete_global_ref")]
    public static extern void DeleteGlobalRef(IntPtr obj);

    [DllImport(LibModManager, EntryPoint = "jnihelper_new_global_ref")]
    public static extern IntPtr NewGlobalRef(IntPtr obj);

    [DllImport(LibModManager, EntryPoint = "jnihelper_new_local_ref")]
    public static extern IntPtr NewLocalRef(IntPtr obj);
    
    /// <summary>
    /// 检查并清除异常
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_check_exception")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CheckException();

    [DllImport(LibModManager, EntryPoint = "jnihelper_clear_exception")]
    public static extern void ClearException();

    /// <summary>
    /// 调用 Java 对象方法（返回对象）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_call_object_method")]
    public static extern IntPtr CallObjectMethod(IntPtr obj, IntPtr methodID);

    /// <summary>
    /// 调用 Java 静态方法（返回对象）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_object_method")]
    public static extern IntPtr CallStaticObjectMethod(IntPtr clazz, IntPtr methodID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_object_method0")]
    public static extern IntPtr CallStaticObjectMethod0(IntPtr clazz, IntPtr methodID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_object_method1")]
    public static extern IntPtr CallStaticObjectMethod1(IntPtr clazz, IntPtr methodID, IntPtr arg1);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_object_method2")]
    public static extern IntPtr CallStaticObjectMethod2(IntPtr clazz, IntPtr methodID, IntPtr arg1, IntPtr arg2);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_object_method3")]
    public static extern IntPtr CallStaticObjectMethod3(IntPtr clazz, IntPtr methodID, IntPtr arg1, IntPtr arg2, int arg3);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_void_method0")]
    public static extern void CallStaticVoidMethod0(IntPtr clazz, IntPtr methodID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_void_method1")]
    public static extern void CallStaticVoidMethod1(IntPtr clazz, IntPtr methodID, [MarshalAs(UnmanagedType.I1)] bool arg1);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_void_method2")]
    public static extern void CallStaticVoidMethod2(IntPtr clazz, IntPtr methodID, IntPtr arg1, IntPtr arg2);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_int_method0")]
    public static extern int CallStaticIntMethod0(IntPtr clazz, IntPtr methodID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_object_method_a")]
    public static extern unsafe IntPtr CallObjectMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_boolean_method_a")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern unsafe bool CallBooleanMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_byte_method_a")]
    public static extern unsafe sbyte CallByteMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_char_method_a")]
    [return: MarshalAs(UnmanagedType.U2)]
    public static extern unsafe char CallCharMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_short_method_a")]
    public static extern unsafe short CallShortMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_int_method_a")]
    public static extern unsafe int CallIntMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_long_method_a")]
    public static extern unsafe long CallLongMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_float_method_a")]
    public static extern unsafe float CallFloatMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_double_method_a")]
    public static extern unsafe double CallDoubleMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_void_method_a")]
    public static extern unsafe void CallVoidMethodA(IntPtr obj, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_object_method_a")]
    public static extern unsafe IntPtr CallStaticObjectMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_boolean_method_a")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern unsafe bool CallStaticBooleanMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_byte_method_a")]
    public static extern unsafe sbyte CallStaticByteMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_char_method_a")]
    [return: MarshalAs(UnmanagedType.U2)]
    public static extern unsafe char CallStaticCharMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_short_method_a")]
    public static extern unsafe short CallStaticShortMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_void_method_a")]
    public static extern unsafe void CallStaticVoidMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_int_method_a")]
    public static extern unsafe int CallStaticIntMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_long_method_a")]
    public static extern unsafe long CallStaticLongMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_float_method_a")]
    public static extern unsafe float CallStaticFloatMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    [DllImport(LibModManager, EntryPoint = "jnihelper_call_static_double_method_a")]
    public static extern unsafe double CallStaticDoubleMethodA(IntPtr clazz, IntPtr methodID, JValue* args);

    /// <summary>
    /// 获取静态对象字段
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_object_field")]
    public static extern IntPtr GetStaticObjectField(IntPtr clazz, IntPtr fieldID);

    /// <summary>
    /// 获取对象实例字段
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_object_field")]
    public static extern IntPtr GetObjectField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_boolean_field")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetBooleanField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_byte_field")]
    public static extern sbyte GetByteField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_char_field")]
    [return: MarshalAs(UnmanagedType.U2)]
    public static extern char GetCharField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_short_field")]
    public static extern short GetShortField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_int_field")]
    public static extern int GetIntField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_long_field")]
    public static extern long GetLongField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_float_field")]
    public static extern float GetFloatField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_double_field")]
    public static extern double GetDoubleField(IntPtr obj, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_object_field")]
    public static extern void SetObjectField(IntPtr obj, IntPtr fieldID, IntPtr value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_boolean_field")]
    public static extern void SetBooleanField(
        IntPtr obj,
        IntPtr fieldID,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_byte_field")]
    public static extern void SetByteField(IntPtr obj, IntPtr fieldID, sbyte value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_char_field")]
    public static extern void SetCharField(
        IntPtr obj,
        IntPtr fieldID,
        [MarshalAs(UnmanagedType.U2)] char value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_short_field")]
    public static extern void SetShortField(IntPtr obj, IntPtr fieldID, short value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_int_field")]
    public static extern void SetIntField(IntPtr obj, IntPtr fieldID, int value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_long_field")]
    public static extern void SetLongField(IntPtr obj, IntPtr fieldID, long value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_float_field")]
    public static extern void SetFloatField(IntPtr obj, IntPtr fieldID, float value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_double_field")]
    public static extern void SetDoubleField(IntPtr obj, IntPtr fieldID, double value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_boolean_field")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool GetStaticBooleanField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_byte_field")]
    public static extern sbyte GetStaticByteField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_char_field")]
    [return: MarshalAs(UnmanagedType.U2)]
    public static extern char GetStaticCharField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_short_field")]
    public static extern short GetStaticShortField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_int_field")]
    public static extern int GetStaticIntField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_long_field")]
    public static extern long GetStaticLongField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_float_field")]
    public static extern float GetStaticFloatField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_static_double_field")]
    public static extern double GetStaticDoubleField(IntPtr clazz, IntPtr fieldID);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_object_field")]
    public static extern void SetStaticObjectField(IntPtr clazz, IntPtr fieldID, IntPtr value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_boolean_field")]
    public static extern void SetStaticBooleanField(
        IntPtr clazz,
        IntPtr fieldID,
        [MarshalAs(UnmanagedType.I1)] bool value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_byte_field")]
    public static extern void SetStaticByteField(IntPtr clazz, IntPtr fieldID, sbyte value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_char_field")]
    public static extern void SetStaticCharField(
        IntPtr clazz,
        IntPtr fieldID,
        [MarshalAs(UnmanagedType.U2)] char value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_short_field")]
    public static extern void SetStaticShortField(IntPtr clazz, IntPtr fieldID, short value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_int_field")]
    public static extern void SetStaticIntField(IntPtr clazz, IntPtr fieldID, int value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_long_field")]
    public static extern void SetStaticLongField(IntPtr clazz, IntPtr fieldID, long value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_float_field")]
    public static extern void SetStaticFloatField(IntPtr clazz, IntPtr fieldID, float value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_static_double_field")]
    public static extern void SetStaticDoubleField(IntPtr clazz, IntPtr fieldID, double value);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_array_length")]
    public static extern int GetArrayLength(IntPtr array);

    [DllImport(LibModManager, EntryPoint = "jnihelper_get_object_array_element")]
    public static extern IntPtr GetObjectArrayElement(IntPtr array, int index);

    [DllImport(LibModManager, EntryPoint = "jnihelper_set_object_array_element")]
    public static extern void SetObjectArrayElement(IntPtr array, int index, IntPtr value);

    /// <summary>
    /// 获取对象的 Java 类
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_object_class")]
    public static extern IntPtr GetObjectClass(IntPtr obj);

    /// <summary>
    /// Surface 对象转 ANativeWindow（指针形式返回）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_surface_to_native_window")]
    public static extern IntPtr SurfaceToNativeWindow(IntPtr surface);

    /// <summary>
    /// 从 AInputEvent* 提取 Unicode 字符（JNI KeyEvent.getUnicodeChar）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_keyevent_get_unicode")]
    public static extern uint KeyEventGetUnicode(IntPtr keyEvent);

    /// <summary>
    /// C# → C: 写入 int[] 到指定 key（C# SetData → C buffer → Java nativeGetData）
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_set_data")]
    public static extern void SetData(
        [MarshalAs(UnmanagedType.LPStr)] string key, IntPtr data, int len);

    /// <summary>
    /// C ←: 获取指定 key 的数据长度
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_data_len")]
    public static extern int GetDataLength([MarshalAs(UnmanagedType.LPStr)] string key);

    /// <summary>
    /// C ←: 获取指定 key 的数据 buffer 指针
    /// </summary>
    [DllImport(LibModManager, EntryPoint = "jnihelper_get_data_buf")]
    public static extern IntPtr GetDataBuffer([MarshalAs(UnmanagedType.LPStr)] string key);
}
