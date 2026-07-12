using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed class CodexConfigInstaller(string clientVersion)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<InstallResult> ApplyAsync(
        CodexPaths paths,
        ConfigurationPlan plan,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.CodexDirectory);
        var stateDirectory = Path.Combine(paths.CodexDirectory, ".chatgpt-connector");
        var backupRoot = Path.Combine(stateDirectory, "backups");
        Directory.CreateDirectory(backupRoot);
        var lockPath = Path.Combine(stateDirectory, "config.lock");

        await using var operationLock = AcquireLock(lockPath);
        try
        {
            var id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
            var backupDirectory = Path.Combine(backupRoot, id);
            Directory.CreateDirectory(backupDirectory);
            var configBackup = await BackupAsync(paths.ConfigPath, backupDirectory, cancellationToken);
            var authBackup = await BackupAsync(paths.AuthPath, backupDirectory, cancellationToken);
            var appliedConfigPath = Path.Combine(backupDirectory, "applied-config.toml");
            var appliedAuthPath = Path.Combine(backupDirectory, "applied-auth.json");
            await File.WriteAllTextAsync(appliedConfigPath, plan.UpdatedConfigToml, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(appliedAuthPath, plan.UpdatedAuthJson, new UTF8Encoding(false), cancellationToken);
            var manifest = new BackupManifest(
                id,
                DateTimeOffset.UtcNow,
                clientVersion,
                plan.ManagedPaths,
                configBackup,
                authBackup,
                appliedConfigPath,
                Sha256(plan.UpdatedConfigToml),
                appliedAuthPath,
                Sha256(plan.UpdatedAuthJson));
            var manifestPath = Path.Combine(backupDirectory, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken);

            try
            {
                await AtomicWriteAsync(paths.ConfigPath, plan.UpdatedConfigToml, cancellationToken);
                await AtomicWriteAsync(paths.AuthPath, plan.UpdatedAuthJson, cancellationToken);
            }
            catch
            {
                await RestoreFileAsync(configBackup, cancellationToken);
                await RestoreFileAsync(authBackup, cancellationToken);
                throw;
            }
            return new InstallResult(backupDirectory, manifest);
        }
        finally
        {
            operationLock.Close();
            File.Delete(lockPath);
        }
    }

    private static FileStream AcquireLock(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (File.Exists(lockPath))
        {
            throw new InvalidOperationException("另一个连接器进程正在修改 Codex 配置。", error);
        }
    }

    private static async Task<BackupFile> BackupAsync(
        string sourcePath,
        string backupDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) return new BackupFile(sourcePath, null, null, false);
        var content = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        var backupPath = Path.Combine(backupDirectory, Path.GetFileName(sourcePath));
        await File.WriteAllBytesAsync(backupPath, content, cancellationToken);
        return new BackupFile(sourcePath, backupPath, Convert.ToHexStringLower(SHA256.HashData(content)), true);
    }

    private static async Task AtomicWriteAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task RestoreFileAsync(BackupFile backup, CancellationToken cancellationToken)
    {
        if (!backup.Existed)
        {
            File.Delete(backup.OriginalPath);
            return;
        }
        var content = await File.ReadAllBytesAsync(backup.BackupPath!, cancellationToken);
        await File.WriteAllBytesAsync(backup.OriginalPath, content, cancellationToken);
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
