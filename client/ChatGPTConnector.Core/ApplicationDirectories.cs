namespace ChatGPTConnector.Core;

public static class ApplicationDirectories
{
    public static string Root { get; } = Path.GetFullPath(AppContext.BaseDirectory);
    public static string Data { get; } = Path.Combine(Root, "data");
    public static string Logs { get; } = Path.Combine(Data, "logs");
    public static string Updates { get; } = Path.Combine(Data, "updates");
    public static string InstalledMarker { get; } = Path.Combine(Root, ".installed");
    public static bool IsInstalled => File.Exists(InstalledMarker);

    public static void EnsureWritable()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Updates);
        var probe = Path.Combine(Data, $".write-test-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
    }
}
