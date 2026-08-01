using System.Runtime.InteropServices;

namespace StArray.ModManager.Android.Native;

/// <summary>
/// Upstream JNI facade. The implementation deliberately forwards to the local
/// helper so Android keeps its existing exception and thread-attachment policy.
/// </summary>
public static unsafe class JniNative
{
    public static nint NewGlobalRef(nint obj) => JniHelperNative.NewGlobalRef(obj);
    public static nint NewLocalRef(nint obj) => JniHelperNative.NewLocalRef(obj);
    public static void DeleteLocalRef(nint obj) => JniHelperNative.DeleteLocalRef(obj);
    public static void DeleteGlobalRef(nint obj) => JniHelperNative.DeleteGlobalRef(obj);
    public static nint GetObjectClass(nint obj) => JniHelperNative.GetObjectClass(obj);

    public static bool CheckException() => JniHelperNative.CheckException();
    public static void ClearException() => JniHelperNative.ClearException();

    public static nint FindClass(string className) => JniHelperNative.FindClass(className);
    public static nint GetMethodID(nint clazz, string methodName, string signature) =>
        JniHelperNative.GetMethodID(clazz, methodName, signature);
    public static nint GetStaticMethodID(nint clazz, string methodName, string signature) =>
        JniHelperNative.GetStaticMethodID(clazz, methodName, signature);
    public static nint GetFieldID(nint clazz, string fieldName, string signature) =>
        JniHelperNative.GetFieldID(clazz, fieldName, signature);
    public static nint GetStaticFieldID(nint clazz, string fieldName, string signature) =>
        JniHelperNative.GetStaticFieldID(clazz, fieldName, signature);

    public static nint NewString(string str) => JniHelperNative.NewString(str);
    public static nint NewStringUtf(string utf) => JniHelperNative.NewStringUtf(utf);
    public static nint GetStringUtfChars(nint jstr) => JniHelperNative.GetStringUtfChars(jstr);
    public static void ReleaseStringUtfChars(nint jstr, nint utf) =>
        JniHelperNative.ReleaseStringUtfChars(jstr, utf);
    public static int GetStringLength(nint jstr) => JniHelperNative.GetStringLength(jstr);

    public static nint CallObjectMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallObjectMethodA(obj, methodID, (JValue*)args);
    public static bool CallBooleanMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallBooleanMethodA(obj, methodID, (JValue*)args);
    public static sbyte CallByteMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallByteMethodA(obj, methodID, (JValue*)args);
    public static char CallCharMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallCharMethodA(obj, methodID, (JValue*)args);
    public static short CallShortMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallShortMethodA(obj, methodID, (JValue*)args);
    public static int CallIntMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallIntMethodA(obj, methodID, (JValue*)args);
    public static long CallLongMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallLongMethodA(obj, methodID, (JValue*)args);
    public static float CallFloatMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallFloatMethodA(obj, methodID, (JValue*)args);
    public static double CallDoubleMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallDoubleMethodA(obj, methodID, (JValue*)args);
    public static void CallVoidMethodA(nint obj, nint methodID, nint args) =>
        JniHelperNative.CallVoidMethodA(obj, methodID, (JValue*)args);

    public static nint CallObjectMethod(nint obj, nint methodID) =>
        JniHelperNative.CallObjectMethod(obj, methodID);

    public static nint CallStaticObjectMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticObjectMethodA(clazz, methodID, (JValue*)args);
    public static bool CallStaticBooleanMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticBooleanMethodA(clazz, methodID, (JValue*)args);
    public static sbyte CallStaticByteMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticByteMethodA(clazz, methodID, (JValue*)args);
    public static char CallStaticCharMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticCharMethodA(clazz, methodID, (JValue*)args);
    public static short CallStaticShortMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticShortMethodA(clazz, methodID, (JValue*)args);
    public static int CallStaticIntMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticIntMethodA(clazz, methodID, (JValue*)args);
    public static long CallStaticLongMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticLongMethodA(clazz, methodID, (JValue*)args);
    public static float CallStaticFloatMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticFloatMethodA(clazz, methodID, (JValue*)args);
    public static double CallStaticDoubleMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticDoubleMethodA(clazz, methodID, (JValue*)args);
    public static void CallStaticVoidMethodA(nint clazz, nint methodID, nint args) =>
        JniHelperNative.CallStaticVoidMethodA(clazz, methodID, (JValue*)args);

    public static nint CallStaticObjectMethod(nint clazz, nint methodID) =>
        JniHelperNative.CallStaticObjectMethod(clazz, methodID);

    public static nint GetObjectField(nint obj, nint fieldID) => JniHelperNative.GetObjectField(obj, fieldID);
    public static bool GetBooleanField(nint obj, nint fieldID) => JniHelperNative.GetBooleanField(obj, fieldID);
    public static sbyte GetByteField(nint obj, nint fieldID) => JniHelperNative.GetByteField(obj, fieldID);
    public static char GetCharField(nint obj, nint fieldID) => JniHelperNative.GetCharField(obj, fieldID);
    public static short GetShortField(nint obj, nint fieldID) => JniHelperNative.GetShortField(obj, fieldID);
    public static int GetIntField(nint obj, nint fieldID) => JniHelperNative.GetIntField(obj, fieldID);
    public static long GetLongField(nint obj, nint fieldID) => JniHelperNative.GetLongField(obj, fieldID);
    public static float GetFloatField(nint obj, nint fieldID) => JniHelperNative.GetFloatField(obj, fieldID);
    public static double GetDoubleField(nint obj, nint fieldID) => JniHelperNative.GetDoubleField(obj, fieldID);

    public static void SetObjectField(nint obj, nint fieldID, nint value) =>
        JniHelperNative.SetObjectField(obj, fieldID, value);
    public static void SetBooleanField(nint obj, nint fieldID, bool value) =>
        JniHelperNative.SetBooleanField(obj, fieldID, value);
    public static void SetByteField(nint obj, nint fieldID, sbyte value) =>
        JniHelperNative.SetByteField(obj, fieldID, value);
    public static void SetCharField(nint obj, nint fieldID, char value) =>
        JniHelperNative.SetCharField(obj, fieldID, value);
    public static void SetShortField(nint obj, nint fieldID, short value) =>
        JniHelperNative.SetShortField(obj, fieldID, value);
    public static void SetIntField(nint obj, nint fieldID, int value) =>
        JniHelperNative.SetIntField(obj, fieldID, value);
    public static void SetLongField(nint obj, nint fieldID, long value) =>
        JniHelperNative.SetLongField(obj, fieldID, value);
    public static void SetFloatField(nint obj, nint fieldID, float value) =>
        JniHelperNative.SetFloatField(obj, fieldID, value);
    public static void SetDoubleField(nint obj, nint fieldID, double value) =>
        JniHelperNative.SetDoubleField(obj, fieldID, value);

    public static nint GetStaticObjectField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticObjectField(clazz, fieldID);
    public static bool GetStaticBooleanField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticBooleanField(clazz, fieldID);
    public static sbyte GetStaticByteField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticByteField(clazz, fieldID);
    public static char GetStaticCharField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticCharField(clazz, fieldID);
    public static short GetStaticShortField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticShortField(clazz, fieldID);
    public static int GetStaticIntField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticIntField(clazz, fieldID);
    public static long GetStaticLongField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticLongField(clazz, fieldID);
    public static float GetStaticFloatField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticFloatField(clazz, fieldID);
    public static double GetStaticDoubleField(nint clazz, nint fieldID) =>
        JniHelperNative.GetStaticDoubleField(clazz, fieldID);

    public static void SetStaticObjectField(nint clazz, nint fieldID, nint value) =>
        JniHelperNative.SetStaticObjectField(clazz, fieldID, value);
    public static void SetStaticBooleanField(nint clazz, nint fieldID, bool value) =>
        JniHelperNative.SetStaticBooleanField(clazz, fieldID, value);
    public static void SetStaticByteField(nint clazz, nint fieldID, sbyte value) =>
        JniHelperNative.SetStaticByteField(clazz, fieldID, value);
    public static void SetStaticCharField(nint clazz, nint fieldID, char value) =>
        JniHelperNative.SetStaticCharField(clazz, fieldID, value);
    public static void SetStaticShortField(nint clazz, nint fieldID, short value) =>
        JniHelperNative.SetStaticShortField(clazz, fieldID, value);
    public static void SetStaticIntField(nint clazz, nint fieldID, int value) =>
        JniHelperNative.SetStaticIntField(clazz, fieldID, value);
    public static void SetStaticLongField(nint clazz, nint fieldID, long value) =>
        JniHelperNative.SetStaticLongField(clazz, fieldID, value);
    public static void SetStaticFloatField(nint clazz, nint fieldID, float value) =>
        JniHelperNative.SetStaticFloatField(clazz, fieldID, value);
    public static void SetStaticDoubleField(nint clazz, nint fieldID, double value) =>
        JniHelperNative.SetStaticDoubleField(clazz, fieldID, value);

    public static int GetArrayLength(nint array) => JniHelperNative.GetArrayLength(array);
    public static nint GetObjectArrayElement(nint array, int index) =>
        JniHelperNative.GetObjectArrayElement(array, index);
    public static void SetObjectArrayElement(nint array, int index, nint value) =>
        JniHelperNative.SetObjectArrayElement(array, index, value);

    public static nint GetCurrentActivity() => JniHelperNative.GetCurrentActivity();
    public static nint GetUnitySurface() => JniHelperNative.GetUnitySurface();
    public static nint SurfaceToNativeWindow(nint surface) => JniHelperNative.SurfaceToNativeWindow(surface);
    public static uint KeyEventGetUnicode(nint keyEvent) => JniHelperNative.KeyEventGetUnicode(keyEvent);

    public static void SetData(string key, nint data, int len) => JniHelperNative.SetData(key, data, len);
    public static int GetDataLength(string key) => JniHelperNative.GetDataLength(key);
    public static nint GetDataBuffer(string key) => JniHelperNative.GetDataBuffer(key);
}
