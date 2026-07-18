using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatGPTConnector.Core;

public sealed record WorkspaceAuditEntry(
    DateTimeOffset Timestamp,
    string ProjectPath,
    string Tool,
    string Summary,
    WorkspaceOperationRisk Risk,
    string Decision,
    string Outcome);

public sealed class WorkspaceAuditStore(string path)
{
    private static readonly object Sync = new();
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int RetainedEntries = 1200;

    public static WorkspaceAuditStore ForApplicationDirectory() =>
        new(Path.Combine(ApplicationDirectories.Logs, "workspace-actions.jsonl"));

    public void Append(WorkspaceAuditEntry entry)
    {
        try
        {
            var safe = entry with { Summary = Redact(entry.Summary) };
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(safe) + Environment.NewLine);
                if (new FileInfo(path).Length > MaxLogBytes) Compact();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public IReadOnlyList<WorkspaceAuditEntry> Load(string projectPath, int limit = 200)
    {
        try
        {
            var normalized = Normalize(projectPath);
            lock (Sync)
            {
                if (!File.Exists(path)) return [];
                return File.ReadLines(path)
                    .Select(Parse)
                    .Where(entry => entry is not null && string.Equals(Normalize(entry.ProjectPath), normalized, StringComparison.OrdinalIgnoreCase))
                    .Cast<WorkspaceAuditEntry>()
                    .TakeLast(Math.Clamp(limit, 1, 1000))
                    .Reverse()
                    .ToArray();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return []; }
    }

    public void Clear(string projectPath)
    {
        try
        {
            var normalized = Normalize(projectPath);
            lock (Sync)
            {
                if (!File.Exists(path)) return;
                var retained = File.ReadLines(path).Select(Parse)
                    .Where(entry => entry is not null && !string.Equals(Normalize(entry.ProjectPath), normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => JsonSerializer.Serialize(entry))
                    .ToArray();
                File.WriteAllLines(path, retained);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private void Compact()
    {
        var retained = File.ReadLines(path).TakeLast(RetainedEntries).ToArray();
        File.WriteAllLines(path, retained);
    }

    private static WorkspaceAuditEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonSerializer.Deserialize<WorkspaceAuditEntry>(line); }
        catch (JsonException) { return null; }
    }

    private static string Normalize(string value) =>
        Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static string Redact(string value)
    {
        var redacted = Regex.Replace(value, @"(?i)\b(sk-[A-Za-z0-9_-]{12,})\b", "[已遮盖密钥]");
        redacted = Regex.Replace(redacted,
            @"(?i)\b(api[_-]?key|access[_-]?token|authorization|password)\s*[:=]\s*([^\s;&|]+)",
            "$1=[已遮盖]");
        return redacted.Length <= 2000 ? redacted : redacted[..2000] + "…";
    }
}
