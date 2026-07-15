using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record ClientUpdate(
    string Version,
    Uri PortableExecutableUri,
    Uri PortableChecksumUri,
    Uri? InstallerUri,
    Uri? InstallerChecksumUri);

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
        var executable = TrustedUri(root, "executableUrl");
        var checksum = TrustedUri(root, "checksumUrl");
        var installer = OptionalTrustedUri(root, "installerUrl");
        var installerChecksum = OptionalTrustedUri(root, "installerChecksumUrl");
        if ((installer is null) != (installerChecksum is null))
            throw new InvalidDataException("更新清单中的安装程序信息不完整。");
        return new(version.TrimStart('v'), executable, checksum, installer, installerChecksum);
    }

    public async Task DownloadAndScheduleAsync(
        ClientUpdate update,
        string currentExecutable,
        int currentProcessId,
        CancellationToken cancellationToken = default)
    {
        ApplicationDirectories.EnsureWritable();
        var useInstaller = ApplicationDirectories.IsInstalled && update.InstallerUri is not null;
        var sourceUri = useInstaller ? update.InstallerUri! : update.PortableExecutableUri;
        var checksumUri = useInstaller ? update.InstallerChecksumUri! : update.PortableChecksumUri;
        var safeVersion = string.Concat(update.Version.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'));
        var downloaded = Path.Combine(ApplicationDirectories.Updates, useInstaller
            ? $"LuodongChat-{safeVersion}-setup.exe"
            : $"LuodongChat-{safeVersion}-portable.exe");
        var bytes = await http.GetByteArrayAsync(sourceUri, cancellationToken);
        await File.WriteAllBytesAsync(downloaded, bytes, cancellationToken);
        var expectedText = await http.GetStringAsync(checksumUri, cancellationToken);
        var expected = expectedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(downloaded);
            throw new InvalidDataException("更新文件完整性校验失败，已取消安装。");
        }

        var script = Path.Combine(ApplicationDirectories.Updates, "install-update.cmd");
        var contents = useInstaller
            ? BuildInstallerScript(downloaded, ApplicationDirectories.Root, currentProcessId)
            : BuildPortableInstallScript(downloaded, currentExecutable, currentProcessId);
        await File.WriteAllTextAsync(script, contents, new UTF8Encoding(false), cancellationToken);
        Process.Start(new ProcessStartInfo(script) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    }

    internal static string BuildInstallerScript(string installer, string applicationRoot, int currentProcessId)
    {
        var escapedInstaller = EscapeBatchValue(installer);
        var escapedRoot = EscapeBatchValue(applicationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return $"""
            @echo off
            setlocal
            :wait_for_exit
            tasklist /FI "PID eq {currentProcessId}" /NH 2>nul | findstr /R /C:"[ ]{currentProcessId}[ ]" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait_for_exit
            )
            set "installer={escapedInstaller}"
            set "root={escapedRoot}"
            start /wait "" "%installer%" /S "/D=%root%"
            if errorlevel 1 exit /b %errorlevel%
            del /f /q "%installer%" >nul 2>&1
            start "" "%root%\LuodongChat.exe"
            del "%~f0"
            """;
    }

    internal static string BuildPortableInstallScript(string downloaded, string currentExecutable, int currentProcessId)
    {
        var escapedSource = EscapeBatchValue(downloaded);
        var escapedTarget = EscapeBatchValue(currentExecutable);
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

    private static string EscapeBatchValue(string value) => value.Replace("%", "%%");

    private static Uri TrustedUri(JsonElement root, string property) =>
        ValidateTrusted(new Uri(root.GetProperty(property).GetString()!));

    private static Uri? OptionalTrustedUri(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? ValidateTrusted(new Uri(value.GetString()!)) : null;

    private static Uri ValidateTrusted(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "520skx.com")
            throw new InvalidDataException("更新清单包含不受信任的下载地址。");
        return uri;
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
