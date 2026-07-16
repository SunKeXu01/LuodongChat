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
public sealed record PreparedClientUpdate(string Version, string FilePath, bool IsInstaller);

public sealed class ClientUpdateService(HttpClient http)
{
    private static readonly Uri[] UpdateManifests =
    [
        new("https://oss.520skx.com/latest/update.json"),
        new("https://520skx.com/client/update.json")
    ];

    public async Task<ClientUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        HttpRequestException? lastError = null;
        foreach (var manifest in UpdateManifests)
        {
            try { return await CheckManifestAsync(manifest, currentVersion, cancellationToken); }
            catch (HttpRequestException error) { lastError = error; }
        }
        throw lastError ?? new HttpRequestException("无法获取客户端更新清单。");
    }

    private async Task<ClientUpdate?> CheckManifestAsync(Uri manifest, string currentVersion, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(manifest, cancellationToken);
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

    public async Task<PreparedClientUpdate> PrepareAsync(
        ClientUpdate update,
        int bytesPerSecond = 256 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (bytesPerSecond < 32 * 1024) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        ApplicationDirectories.EnsureWritable();
        var useInstaller = ApplicationDirectories.IsInstalled && update.InstallerUri is not null;
        var sourceUri = useInstaller ? update.InstallerUri! : update.PortableExecutableUri;
        var checksumUri = useInstaller ? update.InstallerChecksumUri! : update.PortableChecksumUri;
        var safeVersion = string.Concat(update.Version.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_'));
        var downloaded = Path.Combine(ApplicationDirectories.Updates, useInstaller
            ? $"LuodongChat-{safeVersion}-setup.exe"
            : $"LuodongChat-{safeVersion}-portable.exe");
        var expectedText = await http.GetStringAsync(checksumUri, cancellationToken);
        var expected = expectedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        if (File.Exists(downloaded) && string.Equals(await HashFileAsync(downloaded, cancellationToken), expected, StringComparison.OrdinalIgnoreCase))
            return new(update.Version, downloaded, useInstaller);

        var partial = downloaded + ".part";
        if (File.Exists(partial)) File.Delete(partial);
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, true);
        var buffer = new byte[32 * 1024];
        var transferred = 0L;
        var startedAt = Stopwatch.StartNew();
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            transferred += count;
            var expectedElapsed = TimeSpan.FromSeconds((double)transferred / bytesPerSecond);
            var delay = expectedElapsed - startedAt.Elapsed;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        output.Close();
        var actual = await HashFileAsync(partial, cancellationToken);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException("更新文件完整性校验失败，已取消安装。");
        }
        File.Move(partial, downloaded, true);
        return new(update.Version, downloaded, useInstaller);
    }

    public void SchedulePrepared(PreparedClientUpdate prepared, string currentExecutable, int currentProcessId)
    {
        var script = Path.Combine(ApplicationDirectories.Updates, "install-update.ps1");
        var contents = prepared.IsInstaller
            ? BuildInstallerScript(prepared.FilePath, ApplicationDirectories.Root, currentProcessId)
            : BuildPortableInstallScript(prepared.FilePath, currentExecutable, currentProcessId);
        File.WriteAllText(script, contents, new UTF8Encoding(true));
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        Process.Start(startInfo);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    internal static string BuildInstallerScript(string installer, string applicationRoot, int currentProcessId)
    {
        var escapedInstaller = EscapePowerShellValue(installer);
        var escapedRoot = EscapePowerShellValue(applicationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return $$"""
            $ErrorActionPreference = 'Stop'
            $log = Join-Path '{{escapedRoot}}' 'data\logs\update.log'
            New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
            function Write-UpdateLog([string] $message) {
              Add-Content -LiteralPath $log -Value ("{0:u} {1}" -f (Get-Date), $message) -Encoding UTF8
            }
            Write-UpdateLog '等待旧版本退出。'
            Wait-Process -Id {{currentProcessId}} -ErrorAction SilentlyContinue
            $installer = '{{escapedInstaller}}'
            $root = '{{escapedRoot}}'
            $env:LUODONGCHAT_AUTOSTART = '1'
            try {
              & $installer /S "/D=$root"
              $installerExitCode = $LASTEXITCODE
            } finally {
              Remove-Item Env:LUODONGCHAT_AUTOSTART -ErrorAction SilentlyContinue
            }
            if ($installerExitCode -ne 0) {
              Write-UpdateLog "安装器返回错误代码 $installerExitCode。"
              exit $installerExitCode
            }
            Write-UpdateLog '安装完成，正在确认新版本是否运行。'
            $target = Join-Path $root 'LuodongChat.exe'
            $started = $false
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
              $running = Get-Process -Name 'LuodongChat' -ErrorAction SilentlyContinue
              if ($running) {
                Start-Sleep -Milliseconds 750
                $running = Get-Process -Name 'LuodongChat' -ErrorAction SilentlyContinue
                if ($running) { $started = $true; break }
              }
              if ((Test-Path -LiteralPath $target) -and -not $running) {
                try {
                  $process = Start-Process -FilePath $target -WorkingDirectory $root -PassThru
                  Start-Sleep -Milliseconds 1000
                  if (-not $process.HasExited) { $started = $true; break }
                } catch { }
              }
              Start-Sleep -Milliseconds 500
            }
            if (-not $started) {
              Write-UpdateLog '更新已经安装，但客户端自动启动失败。'
              throw '更新已经安装，但客户端未能自动启动。'
            }
            Write-UpdateLog '新版本已自动启动。'
            Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;
    }

    internal static string BuildPortableInstallScript(string downloaded, string currentExecutable, int currentProcessId)
    {
        var escapedSource = EscapePowerShellValue(downloaded);
        var escapedTarget = EscapePowerShellValue(currentExecutable);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $log = Join-Path (Split-Path -Parent '{{escapedTarget}}') 'data\logs\update.log'
            New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
            function Write-UpdateLog([string] $message) {
              Add-Content -LiteralPath $log -Value ("{0:u} {1}" -f (Get-Date), $message) -Encoding UTF8
            }
            Write-UpdateLog '等待旧版本退出。'
            Wait-Process -Id {{currentProcessId}} -ErrorAction SilentlyContinue
            $source = '{{escapedSource}}'
            $target = '{{escapedTarget}}'
            $backup = "$target.previous"
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup -Force }
            try { Move-Item -LiteralPath $source -Destination $target -Force }
            catch {
              if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $target -Force }
              exit 1
            }
            $root = Split-Path -Parent $target
            $started = $false
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
              try {
                $process = Start-Process -FilePath $target -WorkingDirectory $root -PassThru
                Start-Sleep -Milliseconds 1000
                if (-not $process.HasExited) { $started = $true; break }
              } catch { Start-Sleep -Milliseconds 500 }
            }
            if (-not $started) {
              Write-UpdateLog '便携版更新完成，但客户端自动启动失败。'
              throw '更新已经安装，但客户端未能自动启动。'
            }
            Write-UpdateLog '便携版新版本已自动启动。'
            Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;
    }

    private static string EscapePowerShellValue(string value) => value.Replace("'", "''");

    private static Uri TrustedUri(JsonElement root, string property) =>
        ValidateTrusted(new Uri(root.GetProperty(property).GetString()!));

    private static Uri? OptionalTrustedUri(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? ValidateTrusted(new Uri(value.GetString()!)) : null;

    private static Uri ValidateTrusted(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || (uri.Host != "520skx.com" && uri.Host != "luodongchat.com" && uri.Host != "oss.520skx.com" && uri.Host != "luodongchat-app.oss-cn-beijing.aliyuncs.com"))
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
