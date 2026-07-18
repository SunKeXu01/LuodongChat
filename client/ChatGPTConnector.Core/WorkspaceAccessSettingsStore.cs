using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record WorkspaceAccessSettings(
    WorkspaceAccessMode Mode,
    WorkspaceCustomPermissions Custom,
    IReadOnlyList<string>? AlwaysAllowedCommandHashes = null);

public sealed class WorkspaceAccessSettingsStore(string path)
{
    public static WorkspaceAccessSettingsStore ForApplicationDirectory() =>
        new(Path.Combine(ApplicationDirectories.Data, "workspace-access.json"));

    public WorkspaceAccessSettings Load(string? workspacePath = null)
    {
        try
        {
            if (!File.Exists(path)) return Default();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (workspacePath is not null
                && document.RootElement.TryGetProperty("projects", out var projects)
                && projects.ValueKind == JsonValueKind.Object)
            {
                var normalized = NormalizeWorkspace(workspacePath);
                foreach (var property in projects.EnumerateObject())
                {
                    if (!string.Equals(property.Name, normalized, StringComparison.OrdinalIgnoreCase)) continue;
                    return JsonSerializer.Deserialize<WorkspaceAccessSettings>(property.Value.GetRawText()) ?? Default();
                }
                return Default();
            }
            if (!document.RootElement.TryGetProperty("mode", out var modeElement)) return Default();
            var value = modeElement.GetString();
            var mode = Enum.TryParse<WorkspaceAccessMode>(value, ignoreCase: true, out var parsed)
                ? parsed : WorkspaceAccessMode.RequestApproval;
            var custom = document.RootElement.TryGetProperty("custom", out var element)
                ? JsonSerializer.Deserialize<WorkspaceCustomPermissions>(element.GetRawText()) ?? new WorkspaceCustomPermissions()
                : new WorkspaceCustomPermissions();
            return new WorkspaceAccessSettings(mode, custom);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return Default();
        }
    }

    public void Save(string workspacePath, WorkspaceAccessSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var projects = LoadProjects();
            projects[NormalizeWorkspace(workspacePath)] = settings;
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { version = 2, projects }));
            File.Move(temp, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static WorkspaceAccessSettings Default() =>
        new(WorkspaceAccessMode.RequestApproval, new WorkspaceCustomPermissions());

    private Dictionary<string, WorkspaceAccessSettings> LoadProjects()
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Object)
                return new(StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, WorkspaceAccessSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in projects.EnumerateObject())
            {
                var settings = JsonSerializer.Deserialize<WorkspaceAccessSettings>(property.Value.GetRawText());
                if (settings is not null) result[property.Name] = settings;
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeWorkspace(string workspacePath) =>
        Path.GetFullPath(workspacePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
