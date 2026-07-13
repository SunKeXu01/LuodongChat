using System.Diagnostics;

namespace ChatGPTConnector.Core;

public sealed record ChatGptAppState(bool IsRunning, string? ExecutablePath)
{
    public bool IsInstalled => ExecutablePath is not null;
}

public sealed class ChatGptAppService
{
    public static readonly Uri DownloadUri = new("https://chatgpt.com/download/");
    private static readonly string[] ProcessNames = ["ChatGPT", "Codex"];

    public ChatGptAppState Detect()
    {
        var running = false;
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            try { running |= processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return new(running, FindExecutable());
    }

    public bool Launch()
    {
        var executable = FindExecutable();
        if (executable is not null)
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            return true;
        }
        try
        {
            Process.Start(new ProcessStartInfo("chatgpt://") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    public void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(DownloadUri.ToString()) { UseShellExecute = true });

    public static string? FindExecutable(string? localAppData = null, string? programFiles = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "ChatGPT", "ChatGPT.exe"),
            Path.Combine(localAppData, "Programs", "Codex", "Codex.exe"),
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "ChatGPT.exe"),
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "Codex.exe"),
            Path.Combine(programFiles, "ChatGPT", "ChatGPT.exe"),
            Path.Combine(programFiles, "Codex", "Codex.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
