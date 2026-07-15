using System.Text.Json;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace ChatGPTConnector.Core;

public sealed record CodexEnvironmentState(
    string? PreviousCodexHome,
    string ManagedCodexHome,
    string? DefaultCodexHome = null,
    string? OriginalBackupPath = null);

public sealed class ManagedCodexEnvironment
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGPTConnector");
    private string StatePath => Path.Combine(_root, "codex-environment.json");
    public string ManagedHome => Path.Combine(_root, "codex-home");

    public async Task ActivateAsync(Uri localGateway, string localAccessKey)
    {
        Directory.CreateDirectory(_root);
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User);
        var defaultHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var sourceHome = string.IsNullOrWhiteSpace(previous) ? defaultHome : previous;
        Directory.CreateDirectory(ManagedHome);
        var sourceConfig = Path.Combine(sourceHome, "config.toml");
        var sourceAuth = Path.Combine(sourceHome, "auth.json");
        var config = File.Exists(sourceConfig) ? await File.ReadAllTextAsync(sourceConfig) : string.Empty;
        var auth = File.Exists(sourceAuth) ? await File.ReadAllTextAsync(sourceAuth) : "{}";
        var plan = new CodexConfigPlanner().CreatePlan(config, auth, new ConnectorSettings(localGateway, localAccessKey));
        await new CodexConfigInstaller("managed-home").ApplyAsync(new CodexPaths(ManagedHome, HomeIsCodexDirectory: true), plan);

        var backupPath = PathExists(defaultHome) ? FindAvailableBackupPath(defaultHome) : null;
        var state = new CodexEnvironmentState(previous, ManagedHome, defaultHome, backupPath);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state));
        try
        {
            if (backupPath is not null) Directory.Move(defaultHome, backupPath);
            CreateManagedLink(defaultHome, ManagedHome);
            if (!IsLinkTo(defaultHome, ManagedHome) || !File.Exists(Path.Combine(defaultHome, "config.toml"))
                || !File.Exists(Path.Combine(defaultHome, "auth.json")))
                throw new InvalidOperationException("Codex 受管配置映射校验失败，原配置将自动恢复。");
            await Task.Run(() => {
                Environment.SetEnvironmentVariable("CODEX_HOME", ManagedHome, EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable("CODEX_HOME", ManagedHome, EnvironmentVariableTarget.Process);
                BroadcastEnvironmentChanged();
            });
        }
        catch
        {
            Restore();
            throw;
        }
    }

    public Task RestoreAsync() => Task.Run(Restore);

    public void Restore()
    {
        if (!File.Exists(StatePath)) return;
        CodexEnvironmentState? state = null;
        try { state = JsonSerializer.Deserialize<CodexEnvironmentState>(File.ReadAllText(StatePath)); } catch { }
        if (state?.DefaultCodexHome is not null)
        {
            if (IsLinkTo(state.DefaultCodexHome, state.ManagedCodexHome))
                Directory.Delete(state.DefaultCodexHome);
            if (state.OriginalBackupPath is not null && PathExists(state.OriginalBackupPath) && !PathExists(state.DefaultCodexHome))
                Directory.Move(state.OriginalBackupPath, state.DefaultCodexHome);
        }
        Environment.SetEnvironmentVariable("CODEX_HOME", state?.PreviousCodexHome, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("CODEX_HOME", state?.PreviousCodexHome, EnvironmentVariableTarget.Process);
        BroadcastEnvironmentChanged();
        if (File.Exists(StatePath)) File.Delete(StatePath);
    }

    public bool IsActive => File.Exists(StatePath);

    private static bool PathExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string FindAvailableBackupPath(string defaultHome)
    {
        var candidate = $"{defaultHome}.chatgptconnector-original";
        if (!PathExists(candidate)) return candidate;
        return $"{candidate}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
    }

    internal static void CreateManagedLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (Exception) when (OperatingSystem.IsWindows()) { }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", linkPath, targetPath },
        }) ?? throw new InvalidOperationException("无法创建 Codex 配置目录联接。");
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"无法创建 Codex 配置目录联接：{process.StandardError.ReadToEnd().Trim()}");
    }

    internal static bool IsLinkTo(string linkPath, string targetPath)
    {
        if (!PathExists(linkPath)) return false;
        try
        {
            var resolved = new DirectoryInfo(linkPath).ResolveLinkTarget(false);
            return resolved is not null && string.Equals(
                Path.GetFullPath(resolved.FullName).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static void BroadcastEnvironmentChanged() => SendMessageTimeout(
        new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
