using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace JALib.Tools;

public static class SimpleHttp
{
    public static Task<byte[]> Get(this HttpClient client, string url)
        => client.GetByteArrayAsync(url);
    public static Task<string> GetString(this HttpClient client, string url)
        => client.GetStringAsync(url);
    public static Task<byte[]> Post(this HttpClient client, string url, byte[] data)
        => ReadBytes(client.PostAsync(url, new ByteArrayContent(data)));
    public static Task<string> PostString(this HttpClient client, string url, byte[] data)
        => ReadString(client.PostAsync(url, new ByteArrayContent(data)));
    public static Task<byte[]> Post(this HttpClient client, string url, string data)
        => ReadBytes(client.PostAsync(url, new StringContent(data)));
    public static Task<string> PostString(this HttpClient client, string url, string data)
        => ReadString(client.PostAsync(url, new StringContent(data)));
    public static Task<byte[]> Post(this HttpClient client, string url, HttpContent data)
        => ReadBytes(client.PostAsync(url, data));
    public static Task<string> PostString(this HttpClient client, string url, HttpContent data)
        => ReadString(client.PostAsync(url, data));
    public static Task<byte[]> Put(this HttpClient client, string url, byte[] data)
        => ReadBytes(client.PutAsync(url, new ByteArrayContent(data)));
    public static Task<string> PutString(this HttpClient client, string url, byte[] data)
        => ReadString(client.PutAsync(url, new ByteArrayContent(data)));
    public static Task<byte[]> Put(this HttpClient client, string url, string data)
        => ReadBytes(client.PutAsync(url, new StringContent(data)));
    public static Task<string> PutString(this HttpClient client, string url, string data)
        => ReadString(client.PutAsync(url, new StringContent(data)));
    public static Task<byte[]> Put(this HttpClient client, string url, HttpContent data)
        => ReadBytes(client.PutAsync(url, data));
    public static Task<string> PutString(this HttpClient client, string url, HttpContent data)
        => ReadString(client.PutAsync(url, data));
    public static Task<byte[]> Delete(this HttpClient client, string url)
        => ReadBytes(client.DeleteAsync(url));
    public static Task<string> DeleteString(this HttpClient client, string url)
        => ReadString(client.DeleteAsync(url));
    public static Task<byte[]> Send(this HttpClient client, HttpRequestMessage request)
        => ReadBytes(client.SendAsync(request));
    public static Task<string> SendString(this HttpClient client, HttpRequestMessage request)
        => ReadString(client.SendAsync(request));
    public static Task<byte[]> Send(
        this HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption)
        => ReadBytes(client.SendAsync(request, completionOption));
    public static Task<string> SendString(
        this HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption)
        => ReadString(client.SendAsync(request, completionOption));
    public static Task<byte[]> Send(
        this HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
        => ReadBytes(client.SendAsync(request, completionOption, cancellationToken));
    public static Task<string> SendString(
        this HttpClient client,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
        => ReadString(client.SendAsync(request, completionOption, cancellationToken));
    public static Task<byte[]> Send(
        this HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => ReadBytes(client.SendAsync(request, cancellationToken));
    public static Task<string> SendString(
        this HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => ReadString(client.SendAsync(request, cancellationToken));

    public static void SetupUserAgent(
        this HttpClient client,
        string appName,
        string appVersion)
    {
        var userAgent = $"{appName}/{appVersion} ({GetOSInfo()})";
        if (client.DefaultRequestHeaders.UserAgent.ToString() == userAgent)
            return;
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
    }

#pragma warning disable SYSLIB0014
    public static Task<byte[]> Get(this WebClient client, string url)
        => client.DownloadDataTaskAsync(url);
    public static Task<string> GetString(this WebClient client, string url)
        => client.DownloadStringTaskAsync(url);
    public static Task<byte[]> Post(this WebClient client, string url, byte[] data)
        => client.UploadDataTaskAsync(url, data);
    public static async Task<string> PostString(this WebClient client, string url, byte[] data)
        => Encoding.UTF8.GetString(await client.UploadDataTaskAsync(url, data).ConfigureAwait(false));
    public static async Task<byte[]> Post(this WebClient client, string url, string data)
        => Encoding.UTF8.GetBytes(await client.UploadStringTaskAsync(url, data).ConfigureAwait(false));
    public static Task<string> PostString(this WebClient client, string url, string data)
        => client.UploadStringTaskAsync(url, data);
    public static Task<byte[]> Put(this WebClient client, string url, byte[] data)
        => client.UploadDataTaskAsync(url, "PUT", data);
    public static async Task<string> PutString(this WebClient client, string url, byte[] data)
        => Encoding.UTF8.GetString(
            await client.UploadDataTaskAsync(url, "PUT", data).ConfigureAwait(false));
    public static async Task<byte[]> Put(this WebClient client, string url, string data)
        => Encoding.UTF8.GetBytes(
            await client.UploadStringTaskAsync(url, "PUT", data).ConfigureAwait(false));
    public static Task<string> PutString(this WebClient client, string url, string data)
        => client.UploadStringTaskAsync(url, "PUT", data);
    public static Task<byte[]> Delete(this WebClient client, string url)
        => client.UploadDataTaskAsync(url, "DELETE", []);
    public static async Task<string> DeleteString(this WebClient client, string url)
        => Encoding.UTF8.GetString(
            await client.UploadDataTaskAsync(url, "DELETE", []).ConfigureAwait(false));
    public static Task<byte[]> Send(
        this WebClient client,
        string method,
        string url,
        byte[] data)
        => client.UploadDataTaskAsync(url, method, data);
    public static async Task<string> SendString(
        this WebClient client,
        string method,
        string url,
        byte[] data)
        => Encoding.UTF8.GetString(
            await client.UploadDataTaskAsync(url, method, data).ConfigureAwait(false));
    public static async Task<byte[]> Send(
        this WebClient client,
        string method,
        string url,
        string data)
        => Encoding.UTF8.GetBytes(
            await client.UploadStringTaskAsync(url, method, data).ConfigureAwait(false));
    public static Task<string> SendString(
        this WebClient client,
        string method,
        string url,
        string data)
        => client.UploadStringTaskAsync(url, method, data);
    public static void SetupUserAgent(
        this WebClient client,
        string appName,
        string appVersion)
        => client.Headers[HttpRequestHeader.UserAgent] =
            $"{appName}/{appVersion} ({GetOSInfo()})";
#pragma warning restore SYSLIB0014

    public static async Task<byte[]> ReadBytes(this Task<HttpResponseMessage> task)
    {
        using var response = await task.ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    public static async Task<string> ReadString(this Task<HttpResponseMessage> task)
    {
        using var response = await task.ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    public static string GetOSInfo()
    {
        var version = Environment.OSVersion.Version;
        if (OperatingSystem.IsAndroid())
            return $"Linux; Android {Math.Max(version.Major, 1)}";
        if (OperatingSystem.IsIOS())
            return $"iPhone; CPU iPhone OS {version.ToString(2).Replace('.', '_')} like Mac OS X";
        if (OperatingSystem.IsWindows())
            return $"Windows NT {version.Major}.{version.Minor}; " +
                   (Environment.Is64BitOperatingSystem ? "Win64; x64" : "WOW64");
        if (OperatingSystem.IsMacOS())
            return $"Macintosh; Intel Mac OS X {version.ToString(3)}";
        if (OperatingSystem.IsLinux())
            return $"X11; Linux {version.Major}.{version.Minor} {RuntimeInformation.ProcessArchitecture}";
        return RuntimeInformation.OSDescription;
    }
}
