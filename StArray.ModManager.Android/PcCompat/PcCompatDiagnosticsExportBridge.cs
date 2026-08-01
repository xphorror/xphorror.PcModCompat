using System.Text.Json;
using StArray.ModManager.Android.Native;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

public static class PcCompatDiagnosticsExportBridge
{
    private static JavaClass? s_bootstrap;
    private static nint s_requestExport;
    private static nint s_getStatus;

    public static void Install()
    {
        try
        {
            s_bootstrap = new JavaClass("com.fizzd.connectedworlds.editorport.StArrayModManagerBootstrap");
            s_requestExport = s_bootstrap.GetStaticMethodID(
                "requestExportDiagnostics",
                "(Ljava/lang/String;Ljava/lang/String;)V");
            s_getStatus = s_bootstrap.GetStaticMethodID(
                "getLastExportStatusJson",
                "()Ljava/lang/String;");
            if (s_requestExport != 0 && s_getStatus != 0)
                PcCompatDiagnosticsExportRuntime.RegisterProvider(RequestExport, GetStatus);
        }
        catch (Exception ex)
        {
            Manager.Logger.Warn(nameof(PcCompatDiagnosticsExportBridge), $"install failed: {ex.Message}");
        }
    }

    private static bool RequestExport(string suggestedName, string content)
    {
        if (s_bootstrap == null || s_requestExport == 0)
            return false;

        var nameRef = s_bootstrap.NewString(suggestedName);
        var contentRef = s_bootstrap.NewString(content);
        if (nameRef == nint.Zero || contentRef == nint.Zero)
        {
            if (nameRef != nint.Zero) JniHelperNative.DeleteLocalRef(nameRef);
            if (contentRef != nint.Zero) JniHelperNative.DeleteLocalRef(contentRef);
            return false;
        }

        try
        {
            s_bootstrap.CallStaticVoidMethod2(s_requestExport, nameRef, contentRef);
            return true;
        }
        finally
        {
            JniHelperNative.DeleteLocalRef(nameRef);
            JniHelperNative.DeleteLocalRef(contentRef);
        }
    }

    private static PcCompatDiagnosticsExportStatus GetStatus()
    {
        if (s_bootstrap == null || s_getStatus == 0)
            return PcCompatDiagnosticsExportStatus.Unavailable;

        var jsonRef = s_bootstrap.CallStaticObjectMethod0(s_getStatus);
        if (jsonRef == nint.Zero)
            return PcCompatDiagnosticsExportStatus.Unavailable;

        try
        {
            var json = JniHelperNative.GetString(jsonRef);
            if (string.IsNullOrWhiteSpace(json))
                return PcCompatDiagnosticsExportStatus.Unavailable;

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new PcCompatDiagnosticsExportStatus
            {
                ProviderAvailable = true,
                Serial = root.TryGetProperty("serial", out var serial) ? serial.GetInt32() : 0,
                State = root.TryGetProperty("state", out var state) ? state.GetString() ?? "Idle" : "Idle",
                Message = root.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty
            };
        }
        finally
        {
            JniHelperNative.DeleteLocalRef(jsonRef);
        }
    }
}
