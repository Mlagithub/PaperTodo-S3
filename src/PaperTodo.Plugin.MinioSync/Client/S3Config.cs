namespace PaperTodo.Plugin.MinioSync.Client;

public sealed record S3Config
{
    public string Endpoint { get; init; } = "";
    public string Bucket { get; init; } = "papertodo";
    public string Prefix { get; init; } = "sync/";
    public string AccessKey { get; init; } = "";
    public string SecretKey { get; init; } = "";
    public string Region { get; init; } = "us-east-1";
    public bool InsecureSkipVerify { get; init; } = false;

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(Bucket) &&
        !string.IsNullOrWhiteSpace(AccessKey) &&
        !string.IsNullOrWhiteSpace(SecretKey);

    public string NormalizedPrefix
    {
        get
        {
            var p = (Prefix ?? "").Trim('/');
            return string.IsNullOrEmpty(p) ? "" : p + "/";
        }
    }
}
