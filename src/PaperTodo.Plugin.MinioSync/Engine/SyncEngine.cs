using PaperTodo.Plugin;
using PaperTodo.Plugin.MinioSync.Client;

namespace PaperTodo.Plugin.MinioSync.Engine;

public sealed record SyncResult
{
    public bool Success { get; init; }
    public int PulledCount { get; init; }
    public int PushedCount { get; init; }
    public int ConflictCount { get; init; }
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
                var syncData = CapturePaperData(workspace, paper);
                currentLocalData[paper.Id] = syncData;
                currentLocalHashes[paper.Id] = syncData.ComputeContentHash();
            }

            // 2. Process Pull & Conflict Detection if remote manifest exists
            var targetManifestPapers = new Dictionary<string, PaperSummary>(StringComparer.Ordinal);
            if (remoteManifest != null)
            {
                foreach (var (remoteId, remoteSummary) in remoteManifest.Papers)
                {
                    targetManifestPapers[remoteId] = remoteSummary;

                    if (!currentLocalData.TryGetValue(remoteId, out var localData))
                    {
                        // New paper from remote -> Pull and create
                        var remotePaperBytes = await _s3Client.GetObjectAsync($"{_prefix}v1/papers/{remoteId}.json", ct).ConfigureAwait(false);
                        if (remotePaperBytes != null && PaperSyncData.FromUtf8Bytes(remotePaperBytes) is { } remoteData)
                        {
                            await ApplyRemotePaperAsync(workspace, noteAssets, remoteData, isConflict: false, ct).ConfigureAwait(false);
                            pulledCount++;
                        }
                    }
                    else
                    {
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
            }

            // 3. Refresh local snapshot after pulls/conflicts
            localPapers = workspace.ListPapers();
            currentLocalData.Clear();
            currentLocalHashes.Clear();
            foreach (var paper in localPapers)
            {
                var syncData = CapturePaperData(workspace, paper);
                currentLocalData[paper.Id] = syncData;
                currentLocalHashes[paper.Id] = syncData.ComputeContentHash();
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
                    if (localData.Type == "note" && noteAssets != null && localData.ReferencedAssets.Count > 0)
                    {
                        await _imageSyncer.EnsureAssetsUploadedAsync(
                            noteAssets, _s3Client, _prefix, paperId, localData.ReferencedAssets, ct).ConfigureAwait(false);
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

            // 5. Update remote manifest ONLY IF we actually pushed something, had deletions, or had conflicts to record
            if (pushedCount > 0 || hasLocalDeletions || conflictCount > 0 || remoteManifest == null)
            {
                var newRevision = (remoteManifest?.SyncRevision ?? 0) + 1;
                var newManifest = new SyncManifest
                {
                    SchemaVersion = 1,
                    SyncRevision = newRevision,
                    LastDeviceId = _deviceId,
                    LastDeviceName = _deviceName,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Papers = updatedSummaries
                };

                await _s3Client.PutObjectAsync(manifestKey, newManifest.ToUtf8Bytes(), "application/json", ct).ConfigureAwait(false);
            }

            // Update known hashes tracking
            knownLocalHashes.Clear();
            foreach (var (k, v) in currentLocalHashes)
            {
                knownLocalHashes[k] = v;
            }

            return new SyncResult
            {
                Success = true,
                PulledCount = pulledCount,
                PushedCount = pushedCount,
                ConflictCount = conflictCount
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

    private static PaperSyncData CapturePaperData(IPaperTodoHostApi workspace, PaperSnapshot paper)
    {
        var todos = new List<TodoItemSyncData>();
        var noteContent = "";
        var referencedAssets = new List<string>();

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
                referencedAssets.AddRange(ImageAssetSyncer.ExtractImageIds(noteContent));
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
            ReferencedAssets = referencedAssets
        };
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

            // Handle imported images and rewrite markdown references
            var adjustedContent = remoteData.NoteContent;
            if (noteAssets != null && remoteData.ReferencedAssets.Count > 0)
            {
                foreach (var oldImageId in remoteData.ReferencedAssets)
                {
                    var assetBytes = await _imageSyncer.DownloadAssetAsync(_s3Client, _prefix, oldImageId, ct).ConfigureAwait(false);
                    if (assetBytes != null && assetBytes.Length > 0)
                    {
                        try
                        {
                            var newImage = noteAssets.ImportImage(noteId, "image/png", assetBytes);
                            adjustedContent = ImageAssetSyncer.ReplaceImageId(adjustedContent, oldImageId, newImage.ImageId);
                        }
                        catch
                        {
                            // Keep original reference on import failure
                        }
                    }
                }
            }

            workspace.WriteNote(new WriteNoteRequest
            {
                PaperId = noteId,
                Content = adjustedContent,
                Mode = NoteWriteMode.Replace
            });
        }
    }

    private static async Task UpdateLocalPaperAsync(
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
            workspace.WriteNote(new WriteNoteRequest
            {
                PaperId = localData.Id,
                Content = remoteData.NoteContent,
                Mode = NoteWriteMode.Replace
            });
        }
    }
}
