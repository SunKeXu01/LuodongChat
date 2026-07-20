using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatGPTConnector.Core;

public enum DiagnosticRange { Related, ThirtyMinutes, TwentyFourHours }
public sealed record DiagnosticUploadRecord(string Id, string AppVersion, string Platform, string ErrorCode, long PackageSize, string Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record DiagnosticPackage(byte[] Data, string ErrorCode, string ManifestJson, int FileCount, int RedactedCount);

public static partial class DiagnosticLog
{
    private static readonly object Gate = new();
    public static string LogPath => Path.Combine(ApplicationDirectories.Logs, "app.log");

    public static void Write(string eventType, string errorCode, Exception? error = null, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        try
        {
            Directory.CreateDirectory(ApplicationDirectories.Logs);
            CleanupLocalLogs(TimeSpan.FromDays(14));
            var value = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.Now,
                ["event"] = Sanitize(eventType).Text,
                ["errorCode"] = Sanitize(errorCode).Text,
                ["exceptionType"] = error?.GetType().Name,
                ["error"] = error is null ? null : Sanitize(error.ToString()).Text,
            };
            if (metadata is not null)
                foreach (var item in metadata) value[item.Key] = Sanitize(item.Value?.ToString() ?? "").Text;
            var line = JsonSerializer.Serialize(value) + Environment.NewLine;
            lock (Gate) File.AppendAllText(LogPath, line, new UTF8Encoding(false));
        }
        catch { }
    }

    public static void CleanupLocalLogs(TimeSpan retention)
    {
        if (!Directory.Exists(ApplicationDirectories.Logs)) return;
        var cutoff = DateTime.UtcNow - retention;
        foreach (var path in Directory.EnumerateFiles(ApplicationDirectories.Logs, "*.log"))
            try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); } catch { }
    }

    public static void ClearLocalLogs()
    {
        if (!Directory.Exists(ApplicationDirectories.Logs)) return;
        foreach (var path in Directory.EnumerateFiles(ApplicationDirectories.Logs, "*.log"))
            try { File.Delete(path); } catch { }
    }

    public static (string Text, int Count) Sanitize(string value)
    {
        var count = 0;
        string Replace(Regex regex, string input, string replacement) => regex.Replace(input, _ => { count++; return replacement; });
        var text = Replace(SecretRegex(), value, "$1[REDACTED]");
        text = Replace(TokenRegex(), text, "[REDACTED_TOKEN]");
        text = Replace(EmailRegex(), text, "[REDACTED_EMAIL]");
        text = Replace(WindowsPathRegex(), text, "[REDACTED_PATH]");
        text = Replace(IpRegex(), text, "[REDACTED_IP]");
        return (text, count);
    }

    [GeneratedRegex("(?i)(authorization|api[_-]?key|token|password|cookie|secret)\\s*[:=]\\s*([^\\s,;]+)")]
    private static partial Regex SecretRegex();
    [GeneratedRegex("(?i)\\b(?:sk|usr|gw|adm)_[A-Za-z0-9._-]{8,}\\b")]
    private static partial Regex TokenRegex();
    [GeneratedRegex("(?i)\\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}\\b")]
    private static partial Regex EmailRegex();
    [GeneratedRegex("(?i)\\b[A-Z]:\\\\(?:[^\\r\\n\\t<>:\"|?*]+\\\\?)+")]
    private static partial Regex WindowsPathRegex();
    [GeneratedRegex("\\b(?:\\d{1,3}\\.){3}\\d{1,3}\\b")]
    private static partial Regex IpRegex();
}

public static class DiagnosticPackageBuilder
{
    public static DiagnosticPackage Create(string errorCode, DiagnosticRange range, string? appVersion = null)
    {
        DiagnosticLog.CleanupLocalLogs(TimeSpan.FromDays(14));
        var now = DateTimeOffset.Now;
        var start = range switch { DiagnosticRange.TwentyFourHours => now.AddHours(-24), DiagnosticRange.ThirtyMinutes => now.AddMinutes(-30), _ => now.AddMinutes(-5) };
        var redactedCount = 0;
        var logLines = new List<string>();
        var requestLines = new List<string>();
        var toolLines = new List<string>();
        if (File.Exists(DiagnosticLog.LogPath))
        {
            foreach (var line in File.ReadLines(DiagnosticLog.LogPath).TakeLast(10_000))
            {
                try
                {
                    using var item = JsonDocument.Parse(line);
                    if (item.RootElement.TryGetProperty("timestamp", out var timestamp)
                        && timestamp.TryGetDateTimeOffset(out var parsed) && parsed < start) continue;
                }
                catch { }
                var safe = DiagnosticLog.Sanitize(line); redactedCount += safe.Count; logLines.Add(safe.Text);
                try
                {
                    using var safeItem = JsonDocument.Parse(safe.Text);
                    var eventName = safeItem.RootElement.TryGetProperty("event", out var eventValue) ? eventValue.GetString() : null;
                    if (eventName?.StartsWith("chat_request_", StringComparison.Ordinal) == true) requestLines.Add(safe.Text);
                    else if (eventName == "tool_call") toolLines.Add(safe.Text);
                }
                catch { }
            }
        }
        var version = appVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["createdAt"] = now, ["rangeStart"] = start, ["rangeEnd"] = now,
            ["appVersion"] = version, ["platform"] = "windows", ["errorCode"] = errorCode,
            ["privacy"] = new { conversationContent = false, fileContent = false, credentials = false, absolutePaths = false },
        };
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8))
        {
            Add(archive, "manifest.json", JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Add(archive, "app.log", string.Join(Environment.NewLine, logLines));
            Add(archive, "error.json", JsonSerializer.Serialize(new { errorCode, generatedAt = now }));
            Add(archive, "requests.json", ToJsonArray(requestLines));
            Add(archive, "tools.json", ToJsonArray(toolLines));
            Add(archive, "environment.json", JsonSerializer.Serialize(new {
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
                processorCount = Environment.ProcessorCount, availableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024,
                freeDiskMb = SafeFreeDiskMb(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (output.Length > 20 * 1024 * 1024) throw new InvalidDataException("诊断包超过 20 MB，请缩小诊断范围。");
        var manifestJson = JsonSerializer.Serialize(manifest);
        return new(output.ToArray(), errorCode, manifestJson, 6, redactedCount);
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(content);
    }
    private static string ToJsonArray(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "[]";
        var values = new List<JsonElement>(lines.Count);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }
        return JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true });
    }
    private static long? SafeFreeDiskMb() { try { return new DriveInfo(Path.GetPathRoot(ApplicationDirectories.Root)!).AvailableFreeSpace / 1024 / 1024; } catch { return null; } }
}

public sealed class DiagnosticClient(HttpClient http)
{
    public async Task<DiagnosticUploadRecord> UploadAsync(Uri gateway, string token, DiagnosticPackage package, string appVersion,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Post, new Uri(gateway, "/v1/diagnostics"), token);
        using var form = new MultipartFormDataContent();
        var content = new ProgressByteArrayContent(package.Data, progress); content.Headers.ContentType = new("application/zip");
        form.Add(content, "file", "diagnostic.zip"); form.Add(new StringContent(appVersion), "appVersion");
        form.Add(new StringContent("windows"), "platform"); form.Add(new StringContent(package.ErrorCode), "errorCode");
        form.Add(new StringContent(package.ManifestJson), "manifest"); request.Content = form;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<DiagnosticUploadRecord>(cancellationToken: cancellationToken))!;
    }
    public async Task<IReadOnlyList<DiagnosticUploadRecord>> ListAsync(Uri gateway, string token, CancellationToken cancellationToken = default)
    { using var request=Authorized(HttpMethod.Get,new Uri(gateway,"/v1/diagnostics"),token); using var response=await http.SendAsync(request,cancellationToken); await EnsureSuccess(response,cancellationToken); return await response.Content.ReadFromJsonAsync<DiagnosticUploadRecord[]>(cancellationToken:cancellationToken) ?? []; }
    public async Task DeleteAsync(Uri gateway, string token, string id, CancellationToken cancellationToken = default)
    { using var request=Authorized(HttpMethod.Delete,new Uri(gateway,$"/v1/diagnostics/{id}"),token); using var response=await http.SendAsync(request,cancellationToken); await EnsureSuccess(response,cancellationToken); }
    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, string token) => new(method,uri){Headers={Authorization=new AuthenticationHeaderValue("Bearer",token)}};
    private static async Task EnsureSuccess(HttpResponseMessage response,CancellationToken cancellationToken) { if(response.IsSuccessStatusCode)return; var body=await response.Content.ReadAsStringAsync(cancellationToken); throw new InvalidOperationException($"诊断服务返回错误 {(int)response.StatusCode}：{body[..Math.Min(body.Length,200)]}"); }

    private sealed class ProgressByteArrayContent(byte[] data, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { const int size=64*1024; for(var offset=0;offset<data.Length;offset+=size){var count=Math.Min(size,data.Length-offset);await stream.WriteAsync(data.AsMemory(offset,count));progress?.Report((offset+count)/(double)data.Length);} }
        protected override bool TryComputeLength(out long length){length=data.Length;return true;}
    }
}
