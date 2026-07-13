using System.Text.Json;
using System.Runtime.InteropServices;

namespace ChatGPTConnector.Core;

public sealed record CodexEnvironmentState(string? PreviousCodexHome, string ManagedCodexHome);

public sealed class ManagedCodexEnvironment
{
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChatGPTConnector");
    private string StatePath => Path.Combine(_root, "codex-environment.json");
    public string ManagedHome => Path.Combine(_root, "codex-home");

    public async Task ActivateAsync(Uri localGateway, string localAccessKey)
    {
        Directory.CreateDirectory(_root);
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME", EnvironmentVariableTarget.User);
        var sourceHome = string.IsNullOrWhiteSpace(previous)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : previous;
        Directory.CreateDirectory(ManagedHome);
        var sourceConfig = Path.Combine(sourceHome, "config.toml");
        var sourceAuth = Path.Combine(sourceHome, "auth.json");
        var config = File.Exists(sourceConfig) ? await File.ReadAllTextAsync(sourceConfig) : string.Empty;
        var auth = File.Exists(sourceAuth) ? await File.ReadAllTextAsync(sourceAuth) : "{}";
        var plan = new CodexConfigPlanner().CreatePlan(config, auth, new ConnectorSettings(localGateway, localAccessKey));
        await new CodexConfigInstaller("managed-home").ApplyAsync(new CodexPaths(ManagedHome), plan);
        await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new CodexEnvironmentState(previous, ManagedHome)));
        Environment.SetEnvironmentVariable("CODEX_HOME", ManagedHome, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("CODEX_HOME", ManagedHome, EnvironmentVariableTarget.Process);
        BroadcastEnvironmentChanged();
    }

    public void Restore()
    {
        if (!File.Exists(StatePath)) return;
        CodexEnvironmentState? state = null;
        try { state = JsonSerializer.Deserialize<CodexEnvironmentState>(File.ReadAllText(StatePath)); } catch { }
        Environment.SetEnvironmentVariable("CODEX_HOME", state?.PreviousCodexHome, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("CODEX_HOME", state?.PreviousCodexHome, EnvironmentVariableTarget.Process);
        BroadcastEnvironmentChanged();
        if (File.Exists(StatePath)) File.Delete(StatePath);
    }

    public bool IsActive => File.Exists(StatePath);

    private static void BroadcastEnvironmentChanged() => SendMessageTimeout(
        new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
