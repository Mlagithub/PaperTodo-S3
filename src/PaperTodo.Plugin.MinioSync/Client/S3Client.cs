using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace PaperTodo.Plugin.MinioSync.Client;

public sealed class S3Client : IDisposable
{
    private readonly S3Config _config;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public S3Client(S3Config config, HttpClient? customClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (customClient != null)
        {
            _httpClient = customClient;
            _disposeClient = false;
        }
        else
        {
            var handler = new HttpClientHandler();
            if (config.InsecureSkipVerify)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _disposeClient = true;
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }

    public Uri BuildObjectUri(string key)
    {
        var baseUri = new Uri(_config.Endpoint.TrimEnd('/') + "/");
        var bucket = Uri.EscapeDataString(_config.Bucket);
        var normalizedKey = key.TrimStart('/');
        return new Uri(baseUri, $"{bucket}/{normalizedKey}");
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!_config.IsValid)
        {
            return false;
        }

        try
        {
            var baseUri = new Uri(_config.Endpoint.TrimEnd('/') + "/");
            var bucketUri = new Uri(baseUri, Uri.EscapeDataString(_config.Bucket));
            using var request = new HttpRequestMessage(HttpMethod.Head, bucketUri);
            S3Signer.Sign(request, _config);
            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
        }
        catch
        {
            return false;
        }
    }

    public async Task<byte[]?> GetObjectAsync(string key, CancellationToken ct = default)
    {
        var uri = BuildObjectUri(key);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        S3Signer.Sign(request, _config);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task PutObjectAsync(
        string key,
        byte[] data,
        string contentType = "application/octet-stream",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var uri = BuildObjectUri(key);
        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new ByteArrayContent(data)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        S3Signer.Sign(request, _config, data);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> HeadObjectAsync(string key, CancellationToken ct = default)
    {
        var uri = BuildObjectUri(key);
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        S3Signer.Sign(request, _config);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        return response.IsSuccessStatusCode;
    }

    public async Task DeleteObjectAsync(string key, CancellationToken ct = default)
    {
        var uri = BuildObjectUri(key);
        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        S3Signer.Sign(request, _config);

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }
}
