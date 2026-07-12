using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace ChatGPTConnector.Core;

public sealed class CodexConfigRestorer
{
    public RestorePlan CreatePlan(
        BackupManifest manifest,
        string currentConfigToml,
        string currentAuthJson)
    {
        var beforeConfig = ReadToml(manifest.Config);
        var appliedConfig = ReadToml(manifest.AppliedConfigPath);
        var currentConfig = ParseToml(currentConfigToml);
        var beforeAuth = ReadJson(manifest.Auth);
        var appliedAuth = ReadJson(manifest.AppliedAuthPath);
        var currentAuth = ParseJson(currentAuthJson);
        var restored = new List<string>();
        var conflicts = new List<string>();

        foreach (var managedPath in manifest.ManagedPaths)
        {
            if (managedPath.StartsWith("auth.", StringComparison.Ordinal))
            {
                RestoreJsonPath(
                    beforeAuth,
                    appliedAuth,
                    currentAuth,
                    managedPath[5..].Split('.'),
                    managedPath,
                    restored,
                    conflicts);
            }
            else
            {
                RestoreTomlPath(
                    beforeConfig,
                    appliedConfig,
                    currentConfig,
                    managedPath.Split('.'),
                    managedPath,
                    restored,
                    conflicts);
            }
        }

        return new RestorePlan(
            TomlSerializer.Serialize(currentConfig),
            currentAuth.ToJsonString(new() { WriteIndented = true }) + Environment.NewLine,
            restored,
            conflicts);
    }

    private static TomlTable ReadToml(BackupFile backup) =>
        backup.Existed ? ReadToml(backup.BackupPath!) : new TomlTable();

    private static TomlTable ReadToml(string path) => ParseToml(File.ReadAllText(path));

    private static TomlTable ParseToml(string content)
    {
        try
        {
            return TomlSerializer.Deserialize<TomlTable>(content) ?? new TomlTable();
        }
        catch (TomlException error)
        {
            throw new InvalidDataException("Codex config.toml is invalid.", error);
        }
    }

    private static JsonObject ReadJson(BackupFile backup) =>
        backup.Existed ? ReadJson(backup.BackupPath!) : new JsonObject();

    private static JsonObject ReadJson(string path) => ParseJson(File.ReadAllText(path));

    private static JsonObject ParseJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return new JsonObject();
        try
        {
            return JsonNode.Parse(content)?.AsObject() ?? new JsonObject();
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Codex auth.json is invalid.", error);
        }
    }

    private static void RestoreTomlPath(
        TomlTable before,
        TomlTable applied,
        TomlTable current,
        string[] path,
        string displayPath,
        List<string> restored,
        List<string> conflicts)
    {
        var beforeValue = GetToml(before, path);
        var appliedValue = GetToml(applied, path);
        var currentValue = GetToml(current, path);
        if (TomlEqual(currentValue, appliedValue))
        {
            SetToml(current, path, beforeValue);
            restored.Add(displayPath);
        }
        else if (!TomlEqual(currentValue, beforeValue)) conflicts.Add(displayPath);
    }

    private static object? GetToml(TomlTable root, IReadOnlyList<string> path)
    {
        object? value = root;
        foreach (var segment in path)
        {
            if (value is not TomlTable table || !table.TryGetValue(segment, out value)) return null;
        }
        return value;
    }

    private static void SetToml(TomlTable root, IReadOnlyList<string> path, object? value)
    {
        var parent = root;
        foreach (var segment in path.Take(path.Count - 1))
        {
            if (!parent.TryGetValue(segment, out var child) || child is not TomlTable childTable)
            {
                childTable = new TomlTable();
                parent[segment] = childTable;
            }
            parent = childTable;
        }
        if (value is null) parent.Remove(path[^1]);
        else parent[path[^1]] = CloneToml(value);
    }

    private static object CloneToml(object value)
    {
        if (value is TomlTable table)
            return TomlSerializer.Deserialize<TomlTable>(TomlSerializer.Serialize(table))!;
        return value;
    }

    private static bool TomlEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        if (left is TomlTable leftTable && right is TomlTable rightTable)
            return TomlSerializer.Serialize(leftTable) == TomlSerializer.Serialize(rightTable);
        return Equals(left, right);
    }

    private static void RestoreJsonPath(
        JsonObject before,
        JsonObject applied,
        JsonObject current,
        string[] path,
        string displayPath,
        List<string> restored,
        List<string> conflicts)
    {
        var beforeValue = GetJson(before, path);
        var appliedValue = GetJson(applied, path);
        var currentValue = GetJson(current, path);
        if (JsonNode.DeepEquals(currentValue, appliedValue))
        {
            SetJson(current, path, beforeValue?.DeepClone());
            restored.Add(displayPath);
        }
        else if (!JsonNode.DeepEquals(currentValue, beforeValue)) conflicts.Add(displayPath);
    }

    private static JsonNode? GetJson(JsonObject root, IReadOnlyList<string> path)
    {
        JsonNode? value = root;
        foreach (var segment in path)
        {
            if (value is not JsonObject valueObject || !valueObject.TryGetPropertyValue(segment, out value)) return null;
        }
        return value;
    }

    private static void SetJson(JsonObject root, IReadOnlyList<string> path, JsonNode? value)
    {
        var parent = root;
        foreach (var segment in path.Take(path.Count - 1))
        {
            if (parent[segment] is not JsonObject child)
            {
                child = new JsonObject();
                parent[segment] = child;
            }
            parent = child;
        }
        if (value is null) parent.Remove(path[^1]);
        else parent[path[^1]] = value;
    }
}
