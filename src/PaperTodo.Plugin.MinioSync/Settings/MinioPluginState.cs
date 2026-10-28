using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperTodo.Plugin.MinioSync.Settings;

public sealed record MinioPluginState
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = "";

    [JsonPropertyName("lastKnownRevision")]
    public long LastKnownRevision { get; init; }

    [JsonPropertyName("lastSyncTime")]
    public DateTimeOffset? LastSyncTime { get; init; }

    [JsonPropertyName("knownLocalHashes")]
    public Dictionary<string, string> KnownLocalHashes { get; init; } = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static MinioPluginState FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MinioPluginState
            {
                DeviceId = Guid.NewGuid().ToString("N")
            };
        }

        try
        {
            var state = JsonSerializer.Deserialize<MinioPluginState>(json, JsonOptions);
            if (state == null || string.IsNullOrWhiteSpace(state.DeviceId))
            {
                return new MinioPluginState
                {
                    DeviceId = Guid.NewGuid().ToString("N")
                };
            }
            return state;
        }
        catch
        {
            return new MinioPluginState
            {
                DeviceId = Guid.NewGuid().ToString("N")
            };
        }
    }

    public string ToJson() =>
        JsonSerializer.Serialize(this, JsonOptions);
}
