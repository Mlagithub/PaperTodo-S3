using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace PaperTodo.Plugin.MinioSync.Client;

public static class S3Signer
{
    private const string Iso8601DateTimeFormat = "yyyyMMddTHHmmssZ";
    private const string Iso8601DateFormat = "yyyyMMdd";
    private const string ServiceName = "s3";

    public static void Sign(
        HttpRequestMessage request,
        S3Config config,
        byte[]? payload = null,
        DateTimeOffset? signingTime = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);

        var time = signingTime ?? DateTimeOffset.UtcNow;
        var amzDate = time.ToString(Iso8601DateTimeFormat, CultureInfo.InvariantCulture);
        var dateStamp = time.ToString(Iso8601DateFormat, CultureInfo.InvariantCulture);

        var payloadBytes = payload ?? Array.Empty<byte>();
        var payloadHash = HashHex(payloadBytes);

        request.Headers.Remove("x-amz-date");
        request.Headers.Add("x-amz-date", amzDate);

        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.Add("x-amz-content-sha256", payloadHash);

        var uri = request.RequestUri ?? throw new InvalidOperationException("RequestUri is null.");
        var hostHeader = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        request.Headers.Host = hostHeader;

        // Collect and sort headers for signing
        var headersToSign = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = hostHeader,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate
        };

        var canonicalHeadersBuilder = new StringBuilder();
        var signedHeadersBuilder = new StringBuilder();
        foreach (var (key, value) in headersToSign)
        {
            var lowerKey = key.ToLowerInvariant();
            canonicalHeadersBuilder.Append(lowerKey).Append(':').Append(value.Trim()).Append('\n');
            if (signedHeadersBuilder.Length > 0)
            {
                signedHeadersBuilder.Append(';');
            }
            signedHeadersBuilder.Append(lowerKey);
        }

        var canonicalHeaders = canonicalHeadersBuilder.ToString();
        var signedHeaders = signedHeadersBuilder.ToString();

        // Canonical URI
        var canonicalUri = GetCanonicalUri(uri);

        // Canonical Query
        var canonicalQuery = GetCanonicalQuery(uri);

        // Canonical Request
        var canonicalRequest = string.Join("\n",
            request.Method.Method,
            canonicalUri,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var canonicalRequestHash = HashHex(Encoding.UTF8.GetBytes(canonicalRequest));

        // String to Sign
        var scope = $"{dateStamp}/{config.Region}/{ServiceName}/aws4_request";
        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256",
            amzDate,
            scope,
            canonicalRequestHash);

        // Calculate Signature
        var signingKey = GetSignatureKey(config.SecretKey, dateStamp, config.Region, ServiceName);
        var signature = HmacHex(signingKey, stringToSign);

        // Authorization Header
        var authHeader = $"AWS4-HMAC-SHA256 Credential={config.AccessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    public static string HashHex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    }

    private static string HmacHex(byte[] key, string data)
    {
        var hash = HmacSha256(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        var kSecret = Encoding.UTF8.GetBytes("AWS4" + key);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, regionName);
        var kService = HmacSha256(kRegion, serviceName);
        return HmacSha256(kService, "aws4_request");
    }

    private static string GetCanonicalUri(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        // Encode segment by segment preserving slashes
        var segments = path.Split('/');
        var encoded = new string[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            encoded[i] = Uri.EscapeDataString(Uri.UnescapeDataString(segments[i]));
        }
        var result = string.Join("/", encoded);
        return result.StartsWith('/') ? result : "/" + result;
    }

    private static string GetCanonicalQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return "";
        }

        var query = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(query))
        {
            return "";
        }

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var sortedPairs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=', 2);
            var key = Uri.EscapeDataString(Uri.UnescapeDataString(parts[0]));
            var val = parts.Length > 1 ? Uri.EscapeDataString(Uri.UnescapeDataString(parts[1])) : "";
            sortedPairs[key] = val;
        }

        var sb = new StringBuilder();
        foreach (var (k, v) in sortedPairs)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(k).Append('=').Append(v);
        }
        return sb.ToString();
    }
}
