using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record LocalConversation(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SyncedChatMessage> Messages,
    string? ProjectPath = null,
    McpToolMode? ToolMode = null,
    IReadOnlyList<string>? SelectedMcpToolNames = null);

public sealed class LocalConversationStore(string root)
{
    public static LocalConversationStore ForApplicationDirectory() =>
        new(Path.Combine(ApplicationDirectories.Data, "conversations"));

    public async Task<IReadOnlyList<LocalConversation>> LoadAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var directory = AccountDirectory(accountId);
        if (!Directory.Exists(directory)) return [];
        var conversations = new List<LocalConversation>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var conversation = await JsonSerializer.DeserializeAsync<LocalConversation>(stream, cancellationToken: cancellationToken);
                if (conversation is not null && Guid.TryParse(conversation.Id, out _)) conversations.Add(conversation);
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return conversations.OrderByDescending(item => item.UpdatedAt).ToArray();
    }

    public async Task SaveAsync(string accountId, LocalConversation conversation, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(conversation.Id, out _)) throw new ArgumentException("Conversation ID must be a UUID.", nameof(conversation));
        var directory = AccountDirectory(accountId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{conversation.Id}.json");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true))
                await JsonSerializer.SerializeAsync(stream, conversation, cancellationToken: cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<GeneratedChatImage> SaveGeneratedImageAsync(
        string accountId, string conversationId, string messageId, GeneratedImageData image,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(conversationId, out _) || !Guid.TryParse(messageId, out _))
            throw new ArgumentException("Conversation and message IDs must be UUIDs.");
        if (image.Base64.Length > 48 * 1024 * 1024) throw new InvalidDataException("生成的图片数据过大。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(image.Base64); }
        catch (FormatException error) { throw new InvalidDataException("生成的图片数据无效。", error); }
        var extension = image.MediaType == "image/webp" ? ".webp" : image.MediaType == "image/jpeg" ? ".jpg" : ".png";
        var relativePath = Path.Combine("images", conversationId, messageId + extension);
        var path = GetImagePath(accountId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new GeneratedChatImage(relativePath.Replace(Path.DirectorySeparatorChar, '/'), image.MediaType);
    }

    public async Task<LocalChatAttachment> SaveAttachmentAsync(
        string accountId, string conversationId, string messageId, string sourcePath,
        string name, string mimeType, long size, string category, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(conversationId, out _) || !Guid.TryParse(messageId, out _))
            throw new ArgumentException("Conversation and message IDs must be UUIDs.");
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension.Length > 12 || extension.Any(character => !char.IsLetterOrDigit(character) && character != '.')) extension = ".bin";
        var relativePath = Path.Combine("attachments", conversationId, $"{messageId}-{Guid.NewGuid():N}" + extension);
        var target = GetAttachmentPath(accountId, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            // Windows does not allow the temporary file to be moved while our own
            // output stream still has it open. Keep both streams in an explicit
            // scope so they are disposed before the atomic move.
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new LocalChatAttachment(relativePath.Replace(Path.DirectorySeparatorChar, '/'), name, mimeType, size, category);
    }

    public string GetImagePath(string accountId, string relativePath)
    {
        var accountDirectory = Path.GetFullPath(AccountDirectory(accountId));
        var path = Path.GetFullPath(Path.Combine(accountDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(accountDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("图片路径无效。");
        return path;
    }

    public string GetAttachmentPath(string accountId, string relativePath)
    {
        var accountDirectory = Path.GetFullPath(AccountDirectory(accountId));
        var path = Path.GetFullPath(Path.Combine(accountDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(accountDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("附件路径无效。");
        return path;
    }

    public Task DeleteAsync(string accountId, string conversationId)
    {
        if (!Guid.TryParse(conversationId, out _)) return Task.CompletedTask;
        var path = Path.Combine(AccountDirectory(accountId), $"{conversationId}.json");
        if (File.Exists(path)) File.Delete(path);
        var imageDirectory = Path.Combine(AccountDirectory(accountId), "images", conversationId);
        if (Directory.Exists(imageDirectory)) Directory.Delete(imageDirectory, true);
        var attachmentDirectory = Path.Combine(AccountDirectory(accountId), "attachments", conversationId);
        if (Directory.Exists(attachmentDirectory)) Directory.Delete(attachmentDirectory, true);
        return Task.CompletedTask;
    }

    private string AccountDirectory(string accountId)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant()[..16];
        return Path.Combine(root, fingerprint);
    }
}
