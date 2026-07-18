using System.Diagnostics;

namespace ChatGPTConnector.Core;

public sealed record WorkspaceApplicationInfo(
    int ProcessId,
    string Path,
    string Arguments,
    DateTimeOffset StartedAt,
    string ProcessContainment);

public static class WorkspaceApplicationRegistry
{
    private sealed record Entry(
        string WorkspaceRoot,
        string RelativePath,
        string Arguments,
        DateTimeOffset StartedAt,
        Process Process,
        CommandProcessContainment.Handle Containment);

    private static readonly object Gate = new();
    private static readonly Dictionary<int, Entry> Entries = [];

    public static WorkspaceApplicationInfo Launch(
        string workspaceRoot,
        string relativePath,
        IReadOnlyList<string> arguments,
        ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("无法启动项目程序。");
        CommandProcessContainment.Handle containment;
        try { containment = CommandProcessContainment.Attach(process, restrictUi: false); }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
        var entry = new Entry(
            NormalizeWorkspace(workspaceRoot), relativePath, JoinArguments(arguments),
            DateTimeOffset.UtcNow, process, containment);
        process.Exited += (_, _) => Remove(process.Id, kill: false);
        lock (Gate) Entries[process.Id] = entry;
        try { if (process.HasExited) Remove(process.Id, kill: false); }
        catch (InvalidOperationException) { Remove(process.Id, kill: false); }
        return ToInfo(entry);
    }

    public static IReadOnlyList<WorkspaceApplicationInfo> List(string workspaceRoot)
    {
        var normalized = NormalizeWorkspace(workspaceRoot);
        List<Entry> entries;
        lock (Gate) entries = Entries.Values.Where(item =>
            string.Equals(item.WorkspaceRoot, normalized, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var entry in entries)
        {
            try { if (entry.Process.HasExited) Remove(entry.Process.Id, kill: false); }
            catch (InvalidOperationException) { Remove(entry.Process.Id, kill: false); }
        }
        lock (Gate)
            return Entries.Values
                .Where(item => string.Equals(item.WorkspaceRoot, normalized, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.StartedAt)
                .Select(ToInfo)
                .ToArray();
    }

    public static bool Stop(string workspaceRoot, int processId)
    {
        var normalized = NormalizeWorkspace(workspaceRoot);
        lock (Gate)
        {
            if (!Entries.TryGetValue(processId, out var entry)
                || !string.Equals(entry.WorkspaceRoot, normalized, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return Remove(processId, kill: true);
    }

    public static void StopAll()
    {
        int[] processIds;
        lock (Gate) processIds = Entries.Keys.ToArray();
        foreach (var processId in processIds) Remove(processId, kill: true);
    }

    private static bool Remove(int processId, bool kill)
    {
        Entry? entry;
        lock (Gate)
        {
            if (!Entries.Remove(processId, out entry)) return false;
        }
        if (kill) TryKill(entry.Process);
        entry.Containment.Dispose();
        entry.Process.Dispose();
        return true;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static WorkspaceApplicationInfo ToInfo(Entry entry) => new(
        entry.Process.Id, entry.RelativePath, entry.Arguments, entry.StartedAt, entry.Containment.Level);

    private static string NormalizeWorkspace(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string JoinArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(argument => argument.Any(char.IsWhiteSpace)
            ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument));
}
