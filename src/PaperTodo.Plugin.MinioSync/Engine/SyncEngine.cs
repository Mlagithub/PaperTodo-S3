using PaperTodo.Plugin;
using PaperTodo.Plugin.MinioSync.Client;

namespace PaperTodo.Plugin.MinioSync.Engine;

public sealed record SyncResult
{
    public bool Success { get; init; }
    public int PulledCount { get; init; }
    public int PushedCount { get; init; }
    public int ConflictCount { get; init; }
    public long NewRevision { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset SyncedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class SyncEngine
{
    private readonly S3Client _s3Client;
    private readonly string _prefix;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly ImageAssetSyncer _imageSyncer = new();

    public SyncEngine(S3Client s3Client, string prefix, string deviceId, string deviceName)
    {
        _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
        _prefix = (prefix ?? "").TrimEnd('/') + "/";
        if (_prefix == "/") _prefix = "";
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N") : deviceId;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
    }

    public async Task<SyncResult> ExecuteSyncAsync(
        IPaperTodoHostApi workspace,
        IPaperNoteAssetsApi? noteAssets,
        long lastKnownRevision,
        Dictionary<string, string> knownLocalHashes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        try
        {
            var manifestKey = $"{_prefix}v1/manifest.json";
            var manifestBytes = await _s3Client.GetObjectAsync(manifestKey, ct).ConfigureAwait(false);
            var remoteManifest = manifestBytes != null ? SyncManifest.FromUtf8Bytes(manifestBytes) : null;

            var pulledCount = 0;
            var conflictCount = 0;
            var pushedCount = 0;

            // 1. Snapshot local state
            var localPapers = workspace.ListPapers();
            var currentLocalData = new Dictionary<string, PaperSyncData>(StringComparer.Ordinal);
            var currentLocalHashes = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var paper in localPapers)
            {
                var syncData = CapturePaperData(workspace, noteAssets, paper);
                currentLocalData[paper.Id] = syncData;
                currentLocalHashes[paper.Id] = syncData.ComputeContentHash();
            }

            // 2. Process Pull & Conflict Detection if remote manifest exists
            var targetManifestPapers = new Dictionary<string, PaperSummary>(StringComparer.Ordinal);
            if (remoteManifest != null)
            {
                foreach (var (remoteId, remoteSummary) in remoteManifest.Papers)
                {
                    if (!currentLocalData.TryGetValue(remoteId, out var localData))
                    {
                        // Check if this paper was previously known and deleted locally
                        if (knownLocalHashes.ContainsKey(remoteId))
                        {
                            // Local deletion: do not resurrect, push step will update remote manifest
                            continue;
                        }

                        // New paper from remote -> Pull and create
                        var remotePaperBytes = await _s3Client.GetObjectAsync($"{_prefix}v1/papers/{remoteId}.json", ct).ConfigureAwait(false);
                        if (remotePaperBytes != null && PaperSyncData.FromUtf8Bytes(remotePaperBytes) is { } remoteData)
                        {
                            await ApplyRemotePaperAsync(workspace, noteAssets, remoteData, isConflict: false, ct).ConfigureAwait(false);
                            pulledCount++;
                        }
                        targetManifestPapers[remoteId] = remoteSummary;
                    }
                    else
                    {
                        targetManifestPapers[remoteId] = remoteSummary;
                        var localHash = currentLocalHashes[remoteId];
                        if (string.Equals(localHash, remoteSummary.ContentHash, StringComparison.Ordinal))
                        {
                            // Exactly identical, nothing to do
                            continue;
                        }

                        // Content differs: check if local or remote was modified compared to last sync
                        knownLocalHashes.TryGetValue(remoteId, out var lastKnownHash);
                        var localModified = !string.Equals(localHash, lastKnownHash, StringComparison.Ordinal);
                        var remoteModified = !string.Equals(remoteSummary.ContentHash, lastKnownHash, StringComparison.Ordinal);

                        // If lastKnownHash wasn't tracked yet, treat remote as truth
                        if (string.IsNullOrEmpty(lastKnownHash))
                        {
                            remoteModified = true;
                            localModified = false;
                        }

                        if (remoteModified && !localModified)
                        {
                            // Remote modified, local unchanged -> Pull update
                            var remotePaperBytes = await _s3Client.GetObjectAsync($"{_prefix}v1/papers/{remoteId}.json", ct).ConfigureAwait(false);
                            if (remotePaperBytes != null && PaperSyncData.FromUtf8Bytes(remotePaperBytes) is { } remoteData)
                            {
                                await UpdateLocalPaperAsync(workspace, noteAssets, localData, remoteData, ct).ConfigureAwait(false);
                                pulledCount++;
                            }
                        }
                        else if (remoteModified && localModified)
                        {
                            // Both local and remote were modified -> Real Conflict!
                            var remotePaperBytes = await _s3Client.GetObjectAsync($"{_prefix}v1/papers/{remoteId}.json", ct).ConfigureAwait(false);
                            if (remotePaperBytes != null && PaperSyncData.FromUtf8Bytes(remotePaperBytes) is { } remoteData)
                            {
                                await ApplyRemotePaperAsync(workspace, noteAssets, remoteData, isConflict: true, ct).ConfigureAwait(false);
                                conflictCount++;
                            }
                        }
                        else
                        {
                            // Local modified, remote unchanged -> Will push in step 4
                            continue;
                        }
                    }
                }

                // Check for papers deleted on REMOTE:
                // Papers that exist locally and in knownLocalHashes, but NOT in remoteManifest.Papers
                foreach (var (localId, localData) in currentLocalData.ToArray())
                {
                    if (knownLocalHashes.TryGetValue(localId, out var lastKnownHash) &&
                        !remoteManifest.Papers.ContainsKey(localId))
                    {
                        var localHash = currentLocalHashes[localId];
                        var localModified = !string.Equals(localHash, lastKnownHash, StringComparison.Ordinal);
                        if (!localModified)
                        {
                            // Remote deleted it and local hasn't modified it -> Delete locally
                            try
                            {
                                workspace.DeletePaper(localId);
                                currentLocalData.Remove(localId);
                                currentLocalHashes.Remove(localId);
                                knownLocalHashes.Remove(localId);
                                pulledCount++;
                            }
                            catch { }
                        }
                    }
                }
            }

            // 3. Refresh local snapshot after pulls/conflicts
            localPapers = workspace.ListPapers();
            currentLocalData.Clear();
            currentLocalHashes.Clear();
            foreach (var paper in localPapers)
            {
                var syncData = CapturePaperData(workspace, noteAssets, paper);
                currentLocalData[paper.Id] = syncData;
                currentLocalHashes[paper.Id] = syncData.ComputeContentHash();
            }

            // Clean knownLocalHashes of deleted papers
            foreach (var id in knownLocalHashes.Keys.ToArray())
            {
                if (!currentLocalData.ContainsKey(id))
                {
                    knownLocalHashes.Remove(id);
                }
            }

            // 4. Push local changes
            var updatedSummaries = new Dictionary<string, PaperSummary>(StringComparer.Ordinal);
            var hasLocalDeletions = remoteManifest != null && remoteManifest.Papers.Keys.Any(k => !currentLocalData.ContainsKey(k));

            foreach (var (paperId, localData) in currentLocalData)
            {
                var hash = currentLocalHashes[paperId];
                var remoteHasSame = remoteManifest != null &&
                                   remoteManifest.Papers.TryGetValue(paperId, out var rSum) &&
                                   string.Equals(rSum.ContentHash, hash, StringComparison.Ordinal);

                if (!remoteHasSame)
                {
                    // Upload referenced images if Note
                    if (localData.Type == "note" && noteAssets != null && localData.AssetHashes.Count > 0)
                    {
                        await _imageSyncer.EnsureAssetsUploadedAsync(
                            noteAssets, _s3Client, _prefix, paperId, localData.AssetHashes, ct).ConfigureAwait(false);
                    }

                    // Upload paper JSON
                    var paperKey = $"{_prefix}v1/papers/{paperId}.json";
                    await _s3Client.PutObjectAsync(paperKey, localData.ToUtf8Bytes(), "application/json", ct).ConfigureAwait(false);
                    pushedCount++;
                }

                updatedSummaries[paperId] = new PaperSummary
                {
                    Type = localData.Type,
                    Title = localData.Title,
                    ContentHash = hash,
                    UpdatedAt = localData.UpdatedAt,
                    ReferencedAssets = localData.ReferencedAssets
                };
            }

            // Clean up S3 paper files for deleted papers
            if (remoteManifest != null)
            {
                foreach (var remoteId in remoteManifest.Papers.Keys)
                {
                    if (!currentLocalData.ContainsKey(remoteId))
                    {
                        try
                        {
                            await _s3Client.DeleteObjectAsync($"{_prefix}v1/papers/{remoteId}.json", ct).ConfigureAwait(false);
                        }
                        catch { }
                    }
                }
            }

            // 5. Update remote manifest ONLY IF we actually pushed something, had deletions, or had conflicts to record
            long finalRevision = remoteManifest?.SyncRevision ?? 0;
            if (pushedCount > 0 || hasLocalDeletions || conflictCount > 0 || remoteManifest == null)
            {
                finalRevision = (remoteManifest?.SyncRevision ?? 0) + 1;
                var newManifest = new SyncManifest
                {
                    SchemaVersion = 1,
                    SyncRevision = finalRevision,
                    LastDeviceId = _deviceId,
                    LastDeviceName = _deviceName,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Papers = updatedSummaries
                };

                await _s3Client.PutObjectAsync(manifestKey, newManifest.ToUtf8Bytes(), "application/json", ct).ConfigureAwait(false);
            }

            // Update known hashes tracking
            foreach (var (k, v) in currentLocalHashes)
            {
                knownLocalHashes[k] = v;
            }

            return new SyncResult
            {
                Success = true,
                PulledCount = pulledCount,
                PushedCount = pushedCount,
                ConflictCount = conflictCount,
                NewRevision = finalRevision
            };
        }
        catch (Exception ex)
        {
            return new SyncResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static PaperSyncData CapturePaperData(
        IPaperTodoHostApi workspace,
        IPaperNoteAssetsApi? noteAssets,
        PaperSnapshot paper)
    {
        var todos = new List<TodoItemSyncData>();
        var noteContent = "";
        var referencedAssets = new List<string>();
        var assetHashes = new Dictionary<string, string>(StringComparer.Ordinal);

        if (paper.Type == "todo")
        {
            var rawTodos = workspace.ListTodos(paper.Id, includeBlank: true);
            foreach (var t in rawTodos)
            {
                todos.Add(new TodoItemSyncData
                {
                    Id = t.Id,
                    Text = t.Text,
                    Done = t.Done,
                    Order = t.Order,
                    ReminderAt = t.ReminderAt,
                    LinkedPaperId = t.LinkedPaperId
                });
            }
        }
        else if (paper.Type == "note")
        {
            var note = workspace.GetNote(paper.Id);
            if (note != null && note.ContentAvailable)
            {
                noteContent = note.Content ?? "";
                var imgIds = ImageAssetSyncer.ExtractImageIds(noteContent);
                foreach (var imgId in imgIds)
                {
                    if (noteAssets != null)
                    {
                        try
                        {
                            var img = noteAssets.ReadImage(paper.Id, imgId);
                            if (img?.Bytes != null && img.Bytes.Length > 0)
                            {
                                var hash = S3Signer.HashHex(img.Bytes);
                                assetHashes[imgId] = hash;
                                if (!referencedAssets.Contains(hash, StringComparer.Ordinal))
                                {
                                    referencedAssets.Add(hash);
                                }
                            }
                        }
                        catch
                        {
                            // If image is unreadable, keep tracking id
                        }
                    }
                }
            }
        }

        return new PaperSyncData
        {
            Id = paper.Id,
            Type = paper.Type,
            Title = paper.Title,
            Todos = todos,
            NoteContent = noteContent,
            UpdatedAt = DateTimeOffset.UtcNow,
            ReferencedAssets = referencedAssets,
            AssetHashes = assetHashes
        };
    }

    private async Task<string> ResolveRemoteNoteContentAsync(
        IPaperNoteAssetsApi? noteAssets,
        string targetPaperId,
        PaperSyncData remoteData,
        CancellationToken ct)
    {
        var content = remoteData.NoteContent;
        if (noteAssets == null || remoteData.AssetHashes.Count == 0)
        {
            return content;
        }

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (oldImageId, hash) in remoteData.AssetHashes)
        {
            if (string.IsNullOrWhiteSpace(hash)) continue;

            var assetBytes = await _imageSyncer.DownloadAssetAsync(_s3Client, _prefix, hash, ct).ConfigureAwait(false);
            if (assetBytes != null && assetBytes.Length > 0)
            {
                try
                {
                    var newImage = noteAssets.ImportImage(targetPaperId, "image/png", assetBytes);
                    replacements[oldImageId] = newImage.ImageId;
                }
                catch
                {
                    // Keep original reference on import failure
                }
            }
        }

        return ImageAssetSyncer.ReplaceImageIds(content, replacements);
    }

    private async Task ApplyRemotePaperAsync(
        IPaperTodoHostApi workspace,
        IPaperNoteAssetsApi? noteAssets,
        PaperSyncData remoteData,
        bool isConflict,
        CancellationToken ct)
    {
        var title = isConflict ? $"[冲突副本] {remoteData.Title}" : remoteData.Title;
        var targetPaperId = isConflict ? null : remoteData.Id;

        if (remoteData.Type == "todo")
        {
            var req = new CreatePaperRequest
            {
                PaperId = targetPaperId,
                Type = "todo",
                Title = title,
                Todos = remoteData.Todos.Select(t => new TodoCreateItem
                {
                    Text = t.Text,
                    Done = t.Done,
                    ReminderAt = t.ReminderAt,
                    LinkedPaperId = t.LinkedPaperId
                }).ToArray()
            };
            workspace.CreatePaper(req);
        }
        else if (remoteData.Type == "note")
        {
            var req = new CreatePaperRequest
            {
                PaperId = targetPaperId,
                Type = "note",
                Title = title
            };
            var result = workspace.CreatePaper(req);
            var noteId = result.PaperId;

            var adjustedContent = await ResolveRemoteNoteContentAsync(noteAssets, noteId, remoteData, ct).ConfigureAwait(false);

            workspace.WriteNote(new WriteNoteRequest
            {
                PaperId = noteId,
                Content = adjustedContent,
                Mode = NoteWriteMode.Replace
            });
        }
    }

    private async Task UpdateLocalPaperAsync(
        IPaperTodoHostApi workspace,
        IPaperNoteAssetsApi? noteAssets,
        PaperSyncData localData,
        PaperSyncData remoteData,
        CancellationToken ct)
    {
        if (localData.Type == "todo")
        {
            // Delete old todos
            foreach (var oldTodo in localData.Todos)
            {
                try
                {
                    workspace.DeleteTodo(new DeleteTodoRequest
                    {
                        PaperId = localData.Id,
                        TodoId = oldTodo.Id
                    });
                }
                catch { }
            }

            // Append remote todos
            if (remoteData.Todos.Count > 0)
            {
                workspace.AppendTodos(new AppendTodosRequest
                {
                    PaperId = localData.Id,
                    Todos = remoteData.Todos.Select(t => new TodoCreateItem
                    {
                        Text = t.Text,
                        Done = t.Done,
                        ReminderAt = t.ReminderAt,
                        LinkedPaperId = t.LinkedPaperId
                    }).ToArray()
                });
            }
        }
        else if (localData.Type == "note")
        {
            var adjustedContent = await ResolveRemoteNoteContentAsync(noteAssets, localData.Id, remoteData, ct).ConfigureAwait(false);

            workspace.WriteNote(new WriteNoteRequest
            {
                PaperId = localData.Id,
                Content = adjustedContent,
                Mode = NoteWriteMode.Replace
            });
        }
    }
}
