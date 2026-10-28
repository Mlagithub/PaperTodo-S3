using System.Text.Json;
using System.Text.Json.Serialization;
using PaperTodo.Plugin.MinioSync.Client;

namespace PaperTodo.Plugin.MinioSync.Settings;

public sealed record MinioPluginSettings
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = "";

    [JsonPropertyName("bucket")]
    public string Bucket { get; init; } = "papertodo";

    [JsonPropertyName("prefix")]
    public string Prefix { get; init; } = "sync/";

    [JsonPropertyName("accessKey")]
    public string AccessKey { get; init; } = "";

    [JsonPropertyName("secretKey")]
    public string SecretKey { get; init; } = "";

    [JsonPropertyName("insecureSkipVerify")]
    public bool InsecureSkipVerify { get; init; } = false;

    [JsonPropertyName("autoSync")]
    public bool AutoSync { get; init; } = true;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static MinioPluginSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new MinioPluginSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<MinioPluginSettings>(json, JsonOptions)
                   ?? new MinioPluginSettings();
        }
        catch
        {
            return new MinioPluginSettings();
        }
    }

    public S3Config ToS3Config() => new()
    {
        Endpoint = Endpoint,
        Bucket = Bucket,
        Prefix = Prefix,
        AccessKey = AccessKey,
        SecretKey = SecretKey,
        InsecureSkipVerify = InsecureSkipVerify
    };
}
