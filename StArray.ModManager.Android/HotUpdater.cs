using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using StArray.ModManager.Android.Native;
using StArray.ModManager.Manager;

namespace StArray.ModManager.Android;

public class HotUpdater
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private const string ProxyPrefix = "https://gh-proxy.org/";

    public string BasePath { get; }
    public string VersionJsonUrl { get; set; }
    public bool UseProxy { get; set; } = true;

    public event Action<string>? OnProgress;

    public HotUpdater(string basePath, string versionJsonUrl)
    {
        BasePath = basePath;
        VersionJsonUrl = versionJsonUrl;
    }

    public class VersionInfo
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("versionCode")] public int VersionCode { get; set; }
        [JsonPropertyName("manager")] public string ManagerUrl { get; set; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("entryAssembly")] public string? EntryAssembly { get; set; }
        [JsonPropertyName("entryMethod")] public string? EntryMethod { get; set; }
    }

    public async Task StartAsync()
    {
        var mgrDir = Path.Combine(BasePath, "manager");
        var runtimeDir = Path.Combine(BasePath, "runtime");
        var modsDir = Path.Combine(BasePath, "mods");
        var localDll = Path.Combine(mgrDir, "StArray.ModManager.dll");
        var localVerFile = Path.Combine(mgrDir, "version.json");

        VersionInfo? localVersion = null;
        if (File.Exists(localDll) && File.Exists(localVerFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(localVerFile);
                localVersion = JsonSerializer.Deserialize<VersionInfo>(json);
                OnProgress?.Invoke($"Local version: {localVersion?.Version} (code={localVersion?.VersionCode})");
            }
            catch (Exception ex)
            {
                Logger.Warn(nameof(HotUpdater), $"Failed to read local version: {ex.Message}");
            }
        }

        if (localVersion != null)
        {
            OnProgress?.Invoke($"Launching existing manager v{localVersion.Version}");
            Launch(mgrDir, runtimeDir, modsDir);
        }

        _ = CheckAndUpdateAsync(localVersion, mgrDir, modsDir);
    }

    private async Task CheckAndUpdateAsync(VersionInfo? local, string mgrDir, string modsDir)
    {
        try
        {
            OnProgress?.Invoke("Fetching remote version.json...");
            var remote = await FetchVersionJsonAsync();
            if (remote == null)
            {
                Logger.Warn(nameof(HotUpdater), "Remote version check failed");
                return;
            }

            bool needUpdate = local == null || local.VersionCode < remote.VersionCode;

            if (!needUpdate)
            {
                OnProgress?.Invoke("Manager is up to date");
                return;
            }

            if (local != null)
                OnProgress?.Invoke($"Update available: v{local.Version} -> v{remote.Version}");
            else
                OnProgress?.Invoke($"First install: downloading v{remote.Version}");

            await DownloadAndExtractAsync(remote, mgrDir);
            RestartApp();
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(HotUpdater), $"Update failed: {ex.Message}");
            OnProgress?.Invoke($"Update failed: {ex.Message}");
        }
    }

    private async Task<VersionInfo?> FetchVersionJsonAsync()
    {
        try
        {
            var url = UseProxy ? ProxyPrefix + VersionJsonUrl : VersionJsonUrl;
            var json = await Client.GetStringAsync(url);
            return JsonSerializer.Deserialize<VersionInfo>(json);
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(HotUpdater), $"Failed to fetch version.json: {ex.Message}");
            return null;
        }
    }

    private async Task DownloadAndExtractAsync(VersionInfo version, string targetDir)
    {
        var zipPath = Path.Combine(BasePath, "manager-download.zip");

        try
        {
            Directory.CreateDirectory(targetDir);

            var url = UseProxy ? ProxyPrefix + version.ManagerUrl : version.ManagerUrl;

            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var total = response.Content.Headers.ContentLength ?? -1;
            var buffer = new byte[8192];
            long read = 0;
            int bytes;
            while ((bytes = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, bytes));
                read += bytes;
                if (total > 0)
                {
                    var pct = (int)(100L * read / total);
                    OnProgress?.Invoke($"Downloading... {pct}%");
                }
            }

            if (!string.IsNullOrEmpty(version.Sha256))
            {
                OnProgress?.Invoke("Verifying SHA-256...");
                var actual = await Sha256Async(zipPath);
                if (!string.Equals(actual, version.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error(nameof(HotUpdater), $"SHA-256 mismatch! expected={version.Sha256} actual={actual}");
                    File.Delete(zipPath);
                    return;
                }
            }

            OnProgress?.Invoke("Extracting...");
            ClearDirectory(targetDir);
            ZipFile.ExtractToDirectory(zipPath, targetDir);

            var verJson = JsonSerializer.Serialize(version);
            await File.WriteAllTextAsync(Path.Combine(targetDir, "version.json"), verJson);

            File.Delete(zipPath);
            OnProgress?.Invoke("Manager installed successfully");
        }
        catch
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            throw;
        }
    }

    private static void Launch(string mgrDir, string rtDir, string modsDir)
    {
        if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);
    }

    private static unsafe void RestartApp()
    {
        Logger.Info(nameof(HotUpdater), "Restarting app...");
        nint activityClass = 0;
        nint packageManager = 0;
        nint packageManagerClass = 0;
        nint packageName = 0;
        nint packageNameArgument = 0;
        nint intent = 0;
        nint intentClass = 0;
        nint addFlagsResult = 0;
        try
        {
            var activity = JniNative.GetCurrentActivity();
            if (activity == IntPtr.Zero) return;

            activityClass = JniNative.GetObjectClass(activity);
            if (activityClass == 0) return;

            // getPackageManager -> PackageManager
            var getPmId = JniNative.GetMethodID(activityClass, "getPackageManager", "()Landroid/content/pm/PackageManager;");
            packageManager = JniNative.CallObjectMethod(activity, getPmId);
            if (packageManager == IntPtr.Zero) return;

            // PackageManager.getLaunchIntentForPackage(String) -> Intent
            packageManagerClass = JniNative.GetObjectClass(packageManager);
            var getIntentId = JniNative.GetMethodID(packageManagerClass, "getLaunchIntentForPackage", "(Ljava/lang/String;)Landroid/content/Intent;");
            packageName = JniNative.CallObjectMethod(
                activity,
                JniNative.GetMethodID(activityClass, "getPackageName", "()Ljava/lang/String;"));
            if (packageName == 0) return;

            packageNameArgument = JniNative.NewStringUtf(GetString(packageName));
            if (packageNameArgument == 0) return;

            var args = stackalloc JValue[1];
            args[0].L = packageNameArgument;
            intent = JniNative.CallObjectMethodA(packageManager, getIntentId, (nint)args);
            if (intent == IntPtr.Zero) return;

            // Intent.addFlags(int)
            intentClass = JniNative.GetObjectClass(intent);
            var addFlagsId = JniNative.GetMethodID(intentClass, "addFlags", "(I)Landroid/content/Intent;");
            args[0].I = 0x4000000 | 0x10000000; // CLEAR_TOP | NEW_TASK
            addFlagsResult = JniNative.CallObjectMethodA(intent, addFlagsId, (nint)args);

            // Activity.startActivity(Intent)
            var startActId = JniNative.GetMethodID(activityClass, "startActivity", "(Landroid/content/Intent;)V");
            args[0].L = intent;
            JniNative.CallVoidMethodA(activity, startActId, (nint)args);

            // Activity.finishAffinity()
            var finishId = JniNative.GetMethodID(activityClass, "finishAffinity", "()V");
            JniNative.CallVoidMethodA(activity, finishId, 0);
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(HotUpdater), $"Restart failed: {ex.Message}");
        }
        finally
        {
            if (addFlagsResult != 0 && addFlagsResult != intent)
                JniNative.DeleteLocalRef(addFlagsResult);
            if (intentClass != 0) JniNative.DeleteLocalRef(intentClass);
            if (intent != 0) JniNative.DeleteLocalRef(intent);
            if (packageNameArgument != 0) JniNative.DeleteLocalRef(packageNameArgument);
            if (packageName != 0) JniNative.DeleteLocalRef(packageName);
            if (packageManagerClass != 0) JniNative.DeleteLocalRef(packageManagerClass);
            if (packageManager != 0) JniNative.DeleteLocalRef(packageManager);
            if (activityClass != 0) JniNative.DeleteLocalRef(activityClass);
        }
    }

    private static string GetString(nint jstr)
    {
        var utf = JniNative.GetStringUtfChars(jstr);
        if (utf == IntPtr.Zero) return "";
        try
        {
            return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(utf) ?? "";
        }
        finally
        {
            JniNative.ReleaseStringUtfChars(jstr, utf);
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(fs);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ClearDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        foreach (var d in Directory.GetDirectories(dir)) ClearDirectory(d);
        Directory.Delete(dir);
    }
}
