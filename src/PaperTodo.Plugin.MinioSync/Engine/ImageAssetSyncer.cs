using System.Text.RegularExpressions;
using PaperTodo.Plugin;
using PaperTodo.Plugin.MinioSync.Client;

namespace PaperTodo.Plugin.MinioSync.Engine;

public sealed class ImageAssetSyncer
{
    private static readonly Regex ImageRefRegex = new(
        @"!\[([^\]]*)\]\(i:([A-Za-z0-9._-]+)\)",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> ExtractImageIds(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (Match match in ImageRefRegex.Matches(markdown))
        {
            if (match.Groups.Count >= 3)
            {
                var id = match.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(id) && !list.Contains(id, StringComparer.Ordinal))
                {
                    list.Add(id);
                }
            }
        }
        return list;
    }

    public static string ReplaceImageId(string markdown, string oldImageId, string newImageId)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(oldImageId))
        {
            return markdown;
        }

        return Regex.Replace(
            markdown,
            $@"(!\[[^\]]*\]\(i:){Regex.Escape(oldImageId)}(\))",
            $"${{1}}{newImageId}${{2}}");
    }

    public async Task<IReadOnlyList<string>> EnsureAssetsUploadedAsync(
        IPaperNoteAssetsApi noteAssets,
        S3Client s3Client,
        string prefix,
        string paperId,
        IEnumerable<string> imageIds,
        CancellationToken ct = default)
    {
        var uploadedHashes = new List<string>();
        foreach (var imageId in imageIds)
        {
            try
            {
                var image = noteAssets.ReadImage(paperId, imageId);
                if (image == null || image.Bytes == null || image.Bytes.Length == 0)
                {
                    continue;
                }

                var hash = S3Signer.HashHex(image.Bytes);
                uploadedHashes.Add(hash);

                var assetKey = $"{prefix}v1/assets/{hash}.bin";
                var exists = await s3Client.HeadObjectAsync(assetKey, ct).ConfigureAwait(false);
                if (!exists)
                {
                    await s3Client.PutObjectAsync(assetKey, image.Bytes, image.Mime, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Ignore missing or unreadable single image to not break entire note sync
            }
        }
        return uploadedHashes;
    }

    public async Task<byte[]?> DownloadAssetAsync(
        S3Client s3Client,
        string prefix,
        string contentHash,
        CancellationToken ct = default)
    {
        var assetKey = $"{prefix}v1/assets/{contentHash}.bin";
        return await s3Client.GetObjectAsync(assetKey, ct).ConfigureAwait(false);
    }
}
