using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin.MinioSync.Engine;

public sealed record PaperSummary
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "todo";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; init; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("referencedAssets")]
    public IReadOnlyList<string> ReferencedAssets { get; init; } = Array.Empty<string>();
}

public sealed record SyncManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("syncRevision")]
    public long SyncRevision { get; init; }

    [JsonPropertyName("lastDeviceId")]
    public string LastDeviceId { get; init; } = "";

    [JsonPropertyName("lastDeviceName")]
    public string LastDeviceName { get; init; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("papers")]
    public Dictionary<string, PaperSummary> Papers { get; init; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public byte[] ToUtf8Bytes() =>
        JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static SyncManifest? FromUtf8Bytes(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncManifest>(bytes, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record TodoItemSyncData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("text")]
    public string Text { get; init; } = "";

    [JsonPropertyName("done")]
    public bool Done { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("reminderAt")]
    public DateTimeOffset? ReminderAt { get; init; }

    [JsonPropertyName("linkedPaperId")]
    public string? LinkedPaperId { get; init; }
}

public sealed record PaperSyncData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "todo";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("todos")]
    public IReadOnlyList<TodoItemSyncData> Todos { get; init; } = Array.Empty<TodoItemSyncData>();

    [JsonPropertyName("noteContent")]
    public string NoteContent { get; init; } = "";

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("referencedAssets")]
    public IReadOnlyList<string> ReferencedAssets { get; init; } = Array.Empty<string>();

    [JsonPropertyName("assetHashes")]
    public Dictionary<string, string> AssetHashes { get; init; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public byte[] ToUtf8Bytes() =>
        JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public static PaperSyncData? FromUtf8Bytes(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<PaperSyncData>(bytes, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public string ComputeContentHash()
    {
        var sb = new StringBuilder();
        sb.Append(Type).Append('|').Append(Title).Append('|');
        if (Type == "todo")
        {
            foreach (var todo in Todos.OrderBy(t => t.Order).ThenBy(t => t.Id, StringComparer.Ordinal))
            {
                sb.Append(todo.Id).Append(':')
                  .Append(todo.Text).Append(':')
                  .Append(todo.Done ? '1' : '0').Append(':')
                  .Append(todo.Order).Append(':')
                  .Append(todo.ReminderAt?.ToUnixTimeMilliseconds() ?? 0).Append(';');
            }
        }
        else
        {
            var normalizedContent = AssetHashes.Count > 0
                ? ImageAssetSyncer.ReplaceImageIds(NoteContent, AssetHashes)
                : NoteContent;
            sb.Append(normalizedContent);
            foreach (var asset in ReferencedAssets.OrderBy(a => a, StringComparer.Ordinal))
            {
                sb.Append('#').Append(asset);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
