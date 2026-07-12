namespace ChatGPTConnector.Core;

public sealed record BackupFile(
    string OriginalPath,
    string? BackupPath,
    string? OriginalSha256,
    bool Existed);

public sealed record BackupManifest(
    string Id,
    DateTimeOffset CreatedAt,
    string ClientVersion,
    IReadOnlyList<string> ManagedPaths,
    BackupFile Config,
    BackupFile Auth,
    string AppliedConfigPath,
    string AppliedConfigSha256,
    string AppliedAuthPath,
    string AppliedAuthSha256);

public sealed record InstallResult(string BackupDirectory, BackupManifest Manifest);

public sealed record RestorePlan(
    string UpdatedConfigToml,
    string UpdatedAuthJson,
    IReadOnlyList<string> RestoredPaths,
    IReadOnlyList<string> Conflicts);
