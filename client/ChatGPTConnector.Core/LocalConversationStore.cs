using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatGPTConnector.Core;

public sealed record LocalConversation(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SyncedChatMessage> Messages);

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

    public Task DeleteAsync(string accountId, string conversationId)
    {
        if (!Guid.TryParse(conversationId, out _)) return Task.CompletedTask;
        var path = Path.Combine(AccountDirectory(accountId), $"{conversationId}.json");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string AccountDirectory(string accountId)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant()[..16];
        return Path.Combine(root, fingerprint);
    }
}
