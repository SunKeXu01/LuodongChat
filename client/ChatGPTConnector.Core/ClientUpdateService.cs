using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record ClientUpdate(string Version, Uri ExecutableUri, Uri ChecksumUri);

public sealed class ClientUpdateService(HttpClient http)
{
    private static readonly Uri UpdateManifest = new("https://520skx.com/client/update.json");

    public async Task<ClientUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(UpdateManifest, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var version = root.GetProperty("version").GetString() ?? string.Empty;
        if (!IsNewer(version, currentVersion)) return null;
        var executable = new Uri(root.GetProperty("executableUrl").GetString()!);
        var checksum = new Uri(root.GetProperty("checksumUrl").GetString()!);
        if (executable.Scheme != Uri.UriSchemeHttps || checksum.Scheme != Uri.UriSchemeHttps
            || executable.Host != "520skx.com" || checksum.Host != "520skx.com")
            throw new InvalidDataException("更新清单包含不受信任的下载地址。");
        return new(version.TrimStart('v'), executable, checksum);
    }

    public async Task DownloadAndScheduleAsync(ClientUpdate update, string currentExecutable, int currentProcessId, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatGPTConnector-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var downloaded = Path.Combine(directory, "ChatGPTConnector.exe");
        var bytes = await http.GetByteArrayAsync(update.ExecutableUri, cancellationToken);
        await File.WriteAllBytesAsync(downloaded, bytes, cancellationToken);
        var expectedText = await http.GetStringAsync(update.ChecksumUri, cancellationToken);
        var expected = expectedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(directory, true);
            throw new InvalidDataException("更新文件完整性校验失败，已取消安装。");
        }
        var script = Path.Combine(directory, "install-update.cmd");
        await File.WriteAllTextAsync(script, BuildInstallScript(downloaded, currentExecutable, currentProcessId), new UTF8Encoding(false), cancellationToken);
        Process.Start(new ProcessStartInfo(script) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    }

    internal static string BuildInstallScript(string downloaded, string currentExecutable, int currentProcessId)
    {
        var escapedSource = downloaded.Replace("%", "%%");
        var escapedTarget = currentExecutable.Replace("%", "%%");
        return $"""
            @echo off
            setlocal
            :wait_for_exit
            tasklist /FI "PID eq {currentProcessId}" /NH 2>nul | findstr /R /C:"[ ]{currentProcessId}[ ]" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait_for_exit
            )
            set "source={escapedSource}"
            set "target={escapedTarget}"
            set "backup={escapedTarget}.previous"
            del /f /q "%backup%" >nul 2>&1
            if exist "%target%" move /y "%target%" "%backup%" >nul
            move /y "%source%" "%target%" >nul
            if errorlevel 1 (
              if exist "%backup%" move /y "%backup%" "%target%" >nul
              exit /b 1
            )
            start "" "%target%"
            del /f /q "%backup%" >nul 2>&1
            del "%~f0"
            """;
    }

    internal static Version NormalizeVersion(string value)
    {
        var numeric = value.Trim().TrimStart('v').Split('-', '+')[0];
        return Version.TryParse(numeric, out var version) ? version : new Version(0, 0, 0);
    }

    internal static bool IsNewer(string candidate, string current)
    {
        var candidateCore = NormalizeVersion(candidate);
        var currentCore = NormalizeVersion(current);
        var coreComparison = candidateCore.CompareTo(currentCore);
        if (coreComparison != 0) return coreComparison > 0;
        return PreviewNumber(candidate) > PreviewNumber(current);
    }

    private static int PreviewNumber(string value)
    {
        const string marker = "preview.";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return int.MaxValue;
        var raw = value[(index + marker.Length)..].Split('+', '-')[0];
        return int.TryParse(raw, out var number) ? number : 0;
    }
}
