namespace ChatGPTConnector.Core;

public sealed record CodexPaths(string HomeDirectory)
{
    public string CodexDirectory => Path.Combine(HomeDirectory, ".codex");
    public string ConfigPath => Path.Combine(CodexDirectory, "config.toml");
    public string AuthPath => Path.Combine(CodexDirectory, "auth.json");

    public static CodexPaths ForCurrentUser() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
