using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using PaperTodo.Plugin;
using PaperTodo.Plugin.MinioSync.Client;
using PaperTodo.Plugin.MinioSync.Engine;

internal static class Program
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static int Main()
    {
        var checks = new (string Name, Action Run)[]
        {
            ("s3-empty-payload-sha256", EmptyPayloadSha256),
            ("s3-signer-headers-and-format", SignerHeadersAndFormat),
            ("s3-uri-builder-normalization", UriBuilderNormalization),
            ("s3-client-mock-operations", MockClientOperations),
            ("manifest-and-sync-data-hash-and-serialization", ManifestSerializationChecks),
            ("sync-engine-serial-relay-push-pull", SerialRelayPushPullChecks),
            ("sync-engine-note-and-image-relay-push-pull", NoteAndImageSyncChecks),
            ("sync-engine-deletion-push-pull", DeletionPushPullChecks),
            ("sync-engine-conflict-branch-isolation", ConflictBranchChecks)
        };

        var failed = 0;
        foreach (var (name, run) in checks)
        {
            try
            {
                run();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {ex}");
            }
        }

        Console.WriteLine($"MinioSync checks: {checks.Length - failed}/{checks.Length} passed.");
        return failed == 0 ? 0 : 1;
    }

    private static void EmptyPayloadSha256()
    {
        var hash = S3Signer.HashHex([]);
        Assert(hash == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            $"Expected standard SHA256 of empty bytes, got: {hash}");
    }

    private static void SignerHeadersAndFormat()
    {
        var config = new S3Config
        {
            Endpoint = "http://192.168.8.4:9000",
            Bucket = "papertodo",
            AccessKey = "minioadmin",
            SecretKey = "minioadmin",
            Region = "us-east-1"
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, "http://192.168.8.4:9000/papertodo/v1/manifest.json");
        var fixedTime = new DateTimeOffset(2026, 3, 30, 12, 0, 0, TimeSpan.Zero);

        S3Signer.Sign(request, config, payload: null, signingTime: fixedTime);

        Assert(request.Headers.Contains("x-amz-date"), "Missing x-amz-date header.");
        Assert(request.Headers.GetValues("x-amz-date").First() == "20260330T120000Z", "x-amz-date format mismatch.");
        Assert(request.Headers.Contains("x-amz-content-sha256"), "Missing x-amz-content-sha256 header.");
        Assert(request.Headers.Contains("Authorization"), "Missing Authorization header.");

        var auth = request.Headers.GetValues("Authorization").First();
        Assert(auth.StartsWith("AWS4-HMAC-SHA256 Credential=minioadmin/20260330/us-east-1/s3/aws4_request"),
            $"Auth header prefix mismatch: {auth}");
        Assert(auth.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date"),
            $"SignedHeaders mismatch: {auth}");
        Assert(auth.Contains("Signature="), "Missing Signature parameter in auth header.");
    }

    private static void UriBuilderNormalization()
    {
        var config = new S3Config
        {
            Endpoint = "http://192.168.8.4:9000",
            Bucket = "papertodo",
            Prefix = "sync/",
            AccessKey = "ak",
            SecretKey = "sk"
        };
        using var client = new S3Client(config);

        var uri1 = client.BuildObjectUri("v1/manifest.json");
        Assert(uri1.ToString() == "http://192.168.8.4:9000/papertodo/v1/manifest.json",
            $"Uri mismatch: {uri1}");

        var uri2 = client.BuildObjectUri("/v1/papers/p1.json");
        Assert(uri2.ToString() == "http://192.168.8.4:9000/papertodo/v1/papers/p1.json",
            $"Uri mismatch: {uri2}");
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Handler != null)
            {
                return Task.FromResult(Handler(request));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static void MockClientOperations()
    {
        var config = new S3Config
        {
            Endpoint = "http://127.0.0.1:9000",
            Bucket = "test-bucket",
            AccessKey = "test-ak",
            SecretKey = "test-sk"
        };

        var handler = new MockHttpHandler();
        using var mockHttpClient = new HttpClient(handler);
        using var client = new S3Client(config, mockHttpClient);

        handler.Handler = req =>
        {
            if (req.RequestUri?.AbsolutePath == "/test-bucket/hello.txt")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello minio"))
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        var bytes = client.GetObjectAsync("hello.txt").GetAwaiter().GetResult();
        Assert(bytes != null && Encoding.UTF8.GetString(bytes) == "hello minio", "GetObject failed.");

        var missing = client.GetObjectAsync("missing.txt").GetAwaiter().GetResult();
        Assert(missing == null, "Expected null on missing object.");

        var putReceived = false;
        handler.Handler = req =>
        {
            if (req.Method == HttpMethod.Put && req.RequestUri?.AbsolutePath == "/test-bucket/upload.bin")
            {
                putReceived = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        };

        client.PutObjectAsync("upload.bin", [1, 2, 3]).GetAwaiter().GetResult();
        Assert(putReceived, "PutObject failed to send expected request.");

        handler.Handler = req => new HttpResponseMessage(req.RequestUri?.AbsolutePath.EndsWith("/exists.txt") == true
            ? HttpStatusCode.OK
            : HttpStatusCode.NotFound);

        Assert(client.HeadObjectAsync("exists.txt").GetAwaiter().GetResult(), "HeadObject exists failed.");
        Assert(!client.HeadObjectAsync("not-exists.txt").GetAwaiter().GetResult(), "HeadObject not-exists failed.");
    }

    private static void ManifestSerializationChecks()
    {
        var manifest = new SyncManifest
        {
            SchemaVersion = 1,
            SyncRevision = 10,
            LastDeviceId = "dev-1",
            LastDeviceName = "PC-A",
            UpdatedAt = DateTimeOffset.UtcNow,
            Papers = new()
            {
                ["p1"] = new PaperSummary
                {
                    Type = "todo",
                    Title = "待办",
                    ContentHash = "hash1",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            }
        };

        var bytes = manifest.ToUtf8Bytes();
        var roundtrip = SyncManifest.FromUtf8Bytes(bytes);
        Assert(roundtrip != null, "Manifest deserialization failed.");
        Assert(roundtrip!.SyncRevision == 10, "SyncRevision mismatch.");
        Assert(roundtrip.Papers.ContainsKey("p1"), "Papers entry missing.");

        var paper = new PaperSyncData
        {
            Id = "p1",
            Type = "todo",
            Title = "待办",
            Todos =
            [
                new() { Id = "t1", Text = "买牛奶", Done = false, Order = 0 }
            ],
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var hash = paper.ComputeContentHash();
        Assert(!string.IsNullOrWhiteSpace(hash), "Hash computation failed.");

        // Hash stability
        Assert(paper.ComputeContentHash() == hash, "Hash must be deterministic.");
    }

    private sealed class InMemoryS3Handler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Storage { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            // Path is /bucket/...
            var parts = path.Split('/', 3, StringSplitOptions.RemoveEmptyEntries);
            var key = parts.Length >= 2 ? parts[1] : "";
            if (parts.Length >= 3) key = parts[1] + "/" + parts[2];

            if (request.Method == HttpMethod.Get)
            {
                if (Storage.TryGetValue(path, out var data))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(data)
                    });
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            if (request.Method == HttpMethod.Put)
            {
                var body = request.Content?.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult() ?? [];
                Storage[path] = body;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            if (request.Method == HttpMethod.Head)
            {
                var exists = Storage.ContainsKey(path);
                return Task.FromResult(new HttpResponseMessage(exists ? HttpStatusCode.OK : HttpStatusCode.NotFound));
            }
            if (request.Method == HttpMethod.Delete)
            {
                Storage.Remove(path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }
    }

    private sealed class MockNoteAssets : IPaperNoteAssetsApi
    {
        private int _counter;
        public Dictionary<string, (string Mime, byte[] Bytes)> Store { get; } = new(StringComparer.Ordinal);

        public PaperNoteImage ReadImage(string paperId, string imageId)
        {
            if (Store.TryGetValue(imageId, out var val))
            {
                return new PaperNoteImage(imageId, val.Mime, val.Bytes);
            }
            throw new FileNotFoundException("Image not found: " + imageId);
        }

        public PaperNoteImage ImportImage(string paperId, string mime, byte[] bytes)
        {
            var id = $"img_{Interlocked.Increment(ref _counter):D8}";
            Store[id] = (mime, bytes);
            return new PaperNoteImage(id, mime, bytes);
        }
    }

    private sealed class MockWorkspace : IPaperTodoHostApi
    {
        public List<PaperSnapshot> Papers { get; } = new();
        public Dictionary<string, List<TodoSnapshot>> Todos { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Notes { get; } = new(StringComparer.Ordinal);

        public IReadOnlySet<string> GrantedPermissions => PaperTodoPermissionNames.All;

        public IReadOnlyList<PaperSnapshot> ListPapers(string? type = null) =>
            Papers.Where(p => type == null || p.Type == type).ToArray();

        public PaperSnapshot? GetPaper(string paperId) =>
            Papers.FirstOrDefault(p => p.Id == paperId);

        public IReadOnlyList<TodoSnapshot> ListTodos(string? paperId = null, bool includeBlank = false)
        {
            if (paperId != null && Todos.TryGetValue(paperId, out var list))
            {
                return list;
            }
            return Todos.Values.SelectMany(x => x).ToArray();
        }

        public NoteSnapshot? GetNote(string paperId)
        {
            var p = GetPaper(paperId);
            if (p == null) return null;
            Notes.TryGetValue(paperId, out var content);
            return new NoteSnapshot(paperId, p.Title, "builtin.markdown", true, content ?? "");
        }

        public PaperMutationResult CreatePaper(CreatePaperRequest request)
        {
            var id = string.IsNullOrWhiteSpace(request.PaperId) ? Guid.NewGuid().ToString("N") : request.PaperId;
            var snap = new PaperSnapshot(id, request.Type, request.Title ?? "Untitled", true, false, false, "builtin.markdown");
            Papers.Add(snap);
            if (request.Type == "todo")
            {
                var todoList = new List<TodoSnapshot>();
                if (request.Todos != null)
                {
                    var order = 0;
                    foreach (var t in request.Todos)
                    {
                        todoList.Add(new TodoSnapshot(id, snap.Title, Guid.NewGuid().ToString("N"), t.Text, t.Done, order++, t.LinkedPaperId, null, t.ReminderAt));
                    }
                }
                Todos[id] = todoList;
            }
            else
            {
                Notes[id] = request.Content ?? "";
            }
            return new PaperMutationResult(id, request.Type, true);
        }

        public AppendTodosResult AppendTodos(AppendTodosRequest request)
        {
            if (!Todos.TryGetValue(request.PaperId, out var list))
            {
                list = new List<TodoSnapshot>();
                Todos[request.PaperId] = list;
            }
            var p = GetPaper(request.PaperId)!;
            var ids = new List<string>();
            var order = list.Count;
            foreach (var t in request.Todos)
            {
                var tid = Guid.NewGuid().ToString("N");
                ids.Add(tid);
                list.Add(new TodoSnapshot(request.PaperId, p.Title, tid, t.Text, t.Done, order++, t.LinkedPaperId, null, t.ReminderAt));
            }
            return new AppendTodosResult(request.PaperId, ids);
        }

        public TodoMutationResult UpdateTodo(UpdateTodoRequest request) =>
            new TodoMutationResult(request.PaperId, request.TodoId);

        public TodoMutationResult SetTodoReminder(SetTodoReminderRequest request) =>
            new TodoMutationResult(request.PaperId, request.TodoId);

        public NoteMutationResult WriteNote(WriteNoteRequest request)
        {
            Notes[request.PaperId] = request.Content;
            return new NoteMutationResult(request.PaperId, request.Content.Length);
        }

        public DeleteMutationResult DeleteTodo(DeleteTodoRequest request)
        {
            if (Todos.TryGetValue(request.PaperId, out var list))
            {
                list.RemoveAll(t => t.Id == request.TodoId);
            }
            return new DeleteMutationResult(request.TodoId, true);
        }

        public DeleteMutationResult DeletePaper(string paperId)
        {
            Papers.RemoveAll(p => p.Id == paperId);
            Todos.Remove(paperId);
            Notes.Remove(paperId);
            return new DeleteMutationResult(paperId, true);
        }

        public IDisposable Subscribe(PaperTodoEventFilter filter, Action<PaperTodoEvent> handler) =>
            new EmptyDisposable();

        private sealed class EmptyDisposable : IDisposable { public void Dispose() { } }
    }

    private static void SerialRelayPushPullChecks()
    {
        var s3Handler = new InMemoryS3Handler();
        using var httpClient = new HttpClient(s3Handler);
        var config = new S3Config
        {
            Endpoint = "http://127.0.0.1:9000",
            Bucket = "papertodo",
            Prefix = "sync/",
            AccessKey = "ak",
            SecretKey = "sk"
        };

        // 1. Device A: Create paper & push
        var clientA = new S3Client(config, httpClient);
        var engineA = new SyncEngine(clientA, "sync/", "device-A", "PC-A");
        var wsA = new MockWorkspace();
        wsA.CreatePaper(new CreatePaperRequest
        {
            Type = "todo",
            Title = "设备A任务",
            Todos = [new() { Text = "写代码", Done = false }]
        });

        var hashesA = new Dictionary<string, string>();
        var resultA = engineA.ExecuteSyncAsync(wsA, null, 0, hashesA).GetAwaiter().GetResult();
        Assert(resultA.Success, $"Device A push failed: {resultA.ErrorMessage}");
        Assert(resultA.PushedCount == 1, "Expected 1 paper pushed from Device A.");

        // 2. Device B: Pull from cloud
        var clientB = new S3Client(config, httpClient);
        var engineB = new SyncEngine(clientB, "sync/", "device-B", "PC-B");
        var wsB = new MockWorkspace();
        var hashesB = new Dictionary<string, string>();

        var resultB = engineB.ExecuteSyncAsync(wsB, null, 0, hashesB).GetAwaiter().GetResult();
        Assert(resultB.Success, $"Device B pull failed: {resultB.ErrorMessage}");
        Assert(resultB.PulledCount == 1, "Expected 1 paper pulled to Device B.");
        Assert(wsB.Papers.Count == 1, "Device B should have 1 paper.");
        Assert(wsB.Papers[0].Title == "设备A任务", $"Title mismatch on Device B: {wsB.Papers[0].Title}");
        Assert(wsB.Todos[wsB.Papers[0].Id][0].Text == "写代码", "Todo text mismatch on Device B.");
    }

    private static void NoteAndImageSyncChecks()
    {
        var s3Handler = new InMemoryS3Handler();
        using var httpClient = new HttpClient(s3Handler);
        var config = new S3Config
        {
            Endpoint = "http://127.0.0.1:9000",
            Bucket = "papertodo",
            Prefix = "sync/",
            AccessKey = "ak",
            SecretKey = "sk"
        };

        var clientA = new S3Client(config, httpClient);
        var engineA = new SyncEngine(clientA, "sync/", "device-A", "PC-A");
        var wsA = new MockWorkspace();
        var assetsA = new MockNoteAssets();
        var img1Bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
        var aImg1 = assetsA.ImportImage("note-1", "image/png", img1Bytes);

        wsA.CreatePaper(new CreatePaperRequest
        {
            PaperId = "note-1",
            Type = "note",
            Title = "图文备忘",
            Content = $"这是图片：![测试图](i:{aImg1.ImageId})"
        });

        var hashesA = new Dictionary<string, string>();
        var resA = engineA.ExecuteSyncAsync(wsA, assetsA, 0, hashesA).GetAwaiter().GetResult();
        Assert(resA.Success, $"Device A push failed: {resA.ErrorMessage}");
        Assert(resA.PushedCount == 1, "Expected 1 note pushed from A.");

        // Device B pulls the note and its image
        var clientB = new S3Client(config, httpClient);
        var engineB = new SyncEngine(clientB, "sync/", "device-B", "PC-B");
        var wsB = new MockWorkspace();
        var assetsB = new MockNoteAssets();
        var hashesB = new Dictionary<string, string>();

        var resB = engineB.ExecuteSyncAsync(wsB, assetsB, 0, hashesB).GetAwaiter().GetResult();
        Assert(resB.Success, $"Device B pull failed: {resB.ErrorMessage}");
        Assert(resB.PulledCount == 1, "Expected 1 note pulled to B.");
        Assert(wsB.Papers.Count == 1, "Device B should have 1 paper.");

        var noteContentOnB = wsB.Notes["note-1"];
        var bImageIds = ImageAssetSyncer.ExtractImageIds(noteContentOnB);
        Assert(bImageIds.Count == 1, $"Expected 1 image reference on B, got {bImageIds.Count}");

        var bImg = assetsB.ReadImage("note-1", bImageIds[0]);
        Assert(bImg.Bytes.SequenceEqual(img1Bytes), "Downloaded image bytes on B mismatch.");

        // Idle sync on Device B: must NOT re-push or trigger infinite ping-pong loop!
        var idleB = engineB.ExecuteSyncAsync(wsB, assetsB, resB.NewRevision, hashesB).GetAwaiter().GetResult();
        Assert(idleB.Success, "Idle sync on B failed.");
        Assert(idleB.PushedCount == 0, $"Idle sync on B should push 0 papers, but pushed {idleB.PushedCount}");
        Assert(idleB.PulledCount == 0, "Idle sync on B should pull 0 papers.");

        // Device A updates the note with a second image
        var img2Bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 5, 6, 7, 8 };
        var aImg2 = assetsA.ImportImage("note-1", "image/png", img2Bytes);
        wsA.WriteNote(new WriteNoteRequest
        {
            PaperId = "note-1",
            Content = $"图1: ![p1](i:{aImg1.ImageId}) 图2: ![p2](i:{aImg2.ImageId})"
        });

        var updateA = engineA.ExecuteSyncAsync(wsA, assetsA, resA.NewRevision, hashesA).GetAwaiter().GetResult();
        Assert(updateA.Success, "Device A note update push failed.");
        Assert(updateA.PushedCount == 1, "Expected 1 paper update pushed from A.");

        // Device B pulls the updated note and imports the second image
        var updateB = engineB.ExecuteSyncAsync(wsB, assetsB, updateA.NewRevision, hashesB).GetAwaiter().GetResult();
        Assert(updateB.Success, "Device B update pull failed.");
        Assert(updateB.PulledCount == 1, "Expected 1 paper update pulled to B.");

        var updatedContentOnB = wsB.Notes["note-1"];
        var updatedBImageIds = ImageAssetSyncer.ExtractImageIds(updatedContentOnB);
        Assert(updatedBImageIds.Count == 2, $"Expected 2 images on B after update, got {updatedBImageIds.Count}");

        var secondImgOnB = assetsB.ReadImage("note-1", updatedBImageIds[1]);
        Assert(secondImgOnB.Bytes.SequenceEqual(img2Bytes), "Second image bytes on B mismatch.");
    }

    private static void DeletionPushPullChecks()
    {
        var s3Handler = new InMemoryS3Handler();
        using var httpClient = new HttpClient(s3Handler);
        var config = new S3Config
        {
            Endpoint = "http://127.0.0.1:9000",
            Bucket = "papertodo",
            Prefix = "sync/",
            AccessKey = "ak",
            SecretKey = "sk"
        };

        var clientA = new S3Client(config, httpClient);
        var engineA = new SyncEngine(clientA, "sync/", "device-A", "PC-A");
        var wsA = new MockWorkspace();
        wsA.CreatePaper(new CreatePaperRequest { PaperId = "p-1", Type = "todo", Title = "任务一" });
        wsA.CreatePaper(new CreatePaperRequest { PaperId = "p-2", Type = "todo", Title = "任务二" });
        var hashesA = new Dictionary<string, string>();

        var resA = engineA.ExecuteSyncAsync(wsA, null, 0, hashesA).GetAwaiter().GetResult();
        Assert(resA.Success && resA.PushedCount == 2, "A initial push failed.");

        var clientB = new S3Client(config, httpClient);
        var engineB = new SyncEngine(clientB, "sync/", "device-B", "PC-B");
        var wsB = new MockWorkspace();
        var hashesB = new Dictionary<string, string>();

        var resB = engineB.ExecuteSyncAsync(wsB, null, 0, hashesB).GetAwaiter().GetResult();
        Assert(resB.Success && resB.PulledCount == 2, "B initial pull failed.");
        Assert(wsB.Papers.Count == 2, "B should have 2 papers.");

        // 1. Device A deletes p-1 locally and pushes
        wsA.DeletePaper("p-1");
        var delPushA = engineA.ExecuteSyncAsync(wsA, null, resA.NewRevision, hashesA).GetAwaiter().GetResult();
        Assert(delPushA.Success, "A delete sync failed.");

        // 2. Device B syncs -> should delete p-1 locally
        var delPullB = engineB.ExecuteSyncAsync(wsB, null, delPushA.NewRevision, hashesB).GetAwaiter().GetResult();
        Assert(delPullB.Success, "B sync deletion failed.");
        Assert(wsB.Papers.Count == 1, $"B should have 1 paper remaining, got {wsB.Papers.Count}");
        Assert(wsB.Papers[0].Id == "p-2", "Remaining paper should be p-2.");

        // 3. Device B deletes p-2 locally and syncs
        wsB.DeletePaper("p-2");
        var delPushB = engineB.ExecuteSyncAsync(wsB, null, delPullB.NewRevision, hashesB).GetAwaiter().GetResult();
        Assert(delPushB.Success, "B delete p-2 failed.");

        // 4. Subsequent sync on Device B -> must NOT resurrect p-2!
        var syncB2 = engineB.ExecuteSyncAsync(wsB, null, delPushB.NewRevision, hashesB).GetAwaiter().GetResult();
        Assert(syncB2.Success, "B sync after delete failed.");
        Assert(wsB.Papers.Count == 0, "B should have 0 papers and not resurrect deleted ones.");
    }

    private static void ConflictBranchChecks()
    {
        var s3Handler = new InMemoryS3Handler();
        using var httpClient = new HttpClient(s3Handler);
        var config = new S3Config
        {
            Endpoint = "http://127.0.0.1:9000",
            Bucket = "papertodo",
            Prefix = "sync/",
            AccessKey = "ak",
            SecretKey = "sk"
        };

        // 1. Initial sync from Device A
        var clientA = new S3Client(config, httpClient);
        var engineA = new SyncEngine(clientA, "sync/", "device-A", "PC-A");
        var wsA = new MockWorkspace();
        wsA.CreatePaper(new CreatePaperRequest
        {
            Type = "note",
            Title = "共享笔记",
            Content = "初始版本"
        });
        var hashesA = new Dictionary<string, string>();
        engineA.ExecuteSyncAsync(wsA, null, 0, hashesA).GetAwaiter().GetResult();

        // 2. Device B pulls the initial version
        var clientB = new S3Client(config, httpClient);
        var engineB = new SyncEngine(clientB, "sync/", "device-B", "PC-B");
        var wsB = new MockWorkspace();
        var hashesB = new Dictionary<string, string>();
        engineB.ExecuteSyncAsync(wsB, null, 0, hashesB).GetAwaiter().GetResult();
        Assert(wsB.Papers.Count == 1, "Device B should have 1 paper.");
        var paperIdOnB = wsB.Papers[0].Id;

        // 3. Device A modifies paper and pushes revision 2
        var paperIdOnA = wsA.Papers[0].Id;
        wsA.WriteNote(new WriteNoteRequest { PaperId = paperIdOnA, Content = "A机更新的内容" });
        var pushA = engineA.ExecuteSyncAsync(wsA, null, 1, hashesA).GetAwaiter().GetResult();
        Assert(pushA.Success, "Device A push update failed.");

        // 4. Device B also modifies paper locally without pulling first (Branching conflict!)
        wsB.WriteNote(new WriteNoteRequest { PaperId = paperIdOnB, Content = "B机本地写的内容" });

        // 5. Device B runs sync -> should detect conflict and create [冲突副本]
        var syncB = engineB.ExecuteSyncAsync(wsB, null, 1, hashesB).GetAwaiter().GetResult();
        Assert(syncB.Success, "Sync B failed.");
        Assert(syncB.ConflictCount == 1, $"Expected 1 conflict, got: {syncB.ConflictCount}");

        // Verify Device B has both papers now: original local and conflict copy
        Assert(wsB.Papers.Count == 2, $"Expected 2 papers on B after conflict, got: {wsB.Papers.Count}");
        var localPaper = wsB.Papers.First(p => p.Id == paperIdOnB);
        Assert(wsB.Notes[localPaper.Id] == "B机本地写的内容", "Local content should remain unchanged!");

        var conflictPaper = wsB.Papers.First(p => p.Id != paperIdOnB);
        Assert(conflictPaper.Title.Contains("[冲突副本]"), $"Expected conflict copy title, got: {conflictPaper.Title}");
    }
}
