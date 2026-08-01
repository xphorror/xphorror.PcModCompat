using System.Runtime.InteropServices;
using System.Text;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

// Android host side of PcCompatTargetSignatureResolver.
//
// The importer runs inside the game process with IL2CPP already loaded, so a target outside the
// hand-audited fixed-op catalog can still get an exact signature - the native export reads it out
// of live metadata. Without this provider the importer leaves such targets unmapped, which is what
// happens on desktop and in tests.
//
// The export performs metadata reads only: no allocation, no managed invoke, nothing that touches
// the GC. That is why it is safe to call from the import worker thread rather than UnityMain.
internal static class PcCompatAndroidTargetSignature
{
    private const string Lib = "starray_modmanager";
    private const string LogTag = "PcCompatTargetSignature";

    // Enough for a long namespace plus a dozen fully qualified parameter types; the export reports
    // -6 rather than truncating, so an undersized buffer surfaces as an audit entry, never as a
    // silently wrong signature.
    private const int RecordCapacity = 4096;

    public static void Install()
        => PcCompatTargetSignatureResolver.RegisterProvider(Resolve);

    private static bool Resolve(
        PcCompatTargetSignatureRequest request,
        out PcCompatResolvedTargetSignature? signature,
        out string error)
    {
        signature = null;
        error = string.Empty;

        var buffer = new byte[RecordCapacity];
        int status;
        try
        {
            status = ResolveTargetSignatureNative(
                request.AssemblyName,
                request.Namespace,
                request.TypeName,
                request.MethodName,
                request.HasArgumentTypeNames ? request.ArgumentTypeNames.Count : -1,
                buffer,
                buffer.Length);
        }
        catch (EntryPointNotFoundException)
        {
            // An older native library in the APK. Report it instead of pretending the target is
            // absent, so the audit says "host too old" rather than "type not found".
            error = "native target signature resolver is unavailable in this build";
            return false;
        }

        var text = ReadUtf8(buffer);
        if (status != 0)
        {
            error = string.IsNullOrEmpty(text)
                ? $"native target signature resolver returned {status}"
                : $"{text} (code {status})";
            return false;
        }

        // assembly \n namespace \n type \n method \n static|instance \n returnType \n param...
        var fields = text.Split('\n');
        if (fields.Length < 6)
        {
            error = $"native target signature record has {fields.Length} fields, expected at least 6";
            return false;
        }

        signature = new PcCompatResolvedTargetSignature
        {
            AssemblyName = fields[0],
            Namespace = fields[1],
            TypeName = fields[2],
            MethodName = fields[3],
            IsStatic = string.Equals(fields[4], "static", StringComparison.Ordinal),
            ReturnType = fields[5],
            ParameterTypes = fields.Length > 6 ? fields[6..] : Array.Empty<string>()
        };

        Logger.Info(LogTag, $"resolved {request} -> {signature}");
        return true;
    }

    private static string ReadUtf8(byte[] buffer)
    {
        var length = Array.IndexOf<byte>(buffer, 0);
        if (length < 0)
            length = buffer.Length;
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(buffer, 0, length);
    }

    [DllImport(Lib, EntryPoint = "modmanager_pccompat_resolve_target_signature")]
    private static extern int ResolveTargetSignatureNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string assemblyName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string namespaceName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string typeName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName,
        int declaredParamCount,
        byte[] output,
        int outputCapacity);
}
