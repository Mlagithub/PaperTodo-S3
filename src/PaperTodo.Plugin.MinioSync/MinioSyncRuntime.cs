using PaperTodo.Plugin;
using PaperTodo.Plugin.MinioSync.Client;
using PaperTodo.Plugin.MinioSync.Engine;
using PaperTodo.Plugin.MinioSync.Settings;

namespace PaperTodo.Plugin.MinioSync;

public sealed class MinioSyncRuntime : IPaperPluginRuntime
{
    private const string ActionIdSyncNow = "minio-sync-now";

    private readonly PaperPluginRuntimeContext _context;
    private readonly IDisposable _workspaceSubscription;
    private readonly IDisposable _settingsSubscription;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Timer _debounceTimer;

    private MinioPluginSettings _settings;
    private MinioPluginState _state;
    private bool _disposed;
    private bool _hasPendingChanges;

    public MinioSyncRuntime(PaperPluginRuntimeContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _settings = MinioPluginSettings.FromJson(context.Settings.Json);
        _state = MinioPluginState.FromJson(context.State.Json);

        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        MinioSyncPlugin.SyncTrigger = manual => TriggerSyncAsync(manual);

        // Register Global TopBar action
        _context.GlobalTopBar.SetActionHandler(OnTopBarAction);
        UpdateTopBarStatus(isSyncing: false, statusText: "MinIO 同步: 就绪 (点击立即同步)");

        _settingsSubscription = context.Settings.Subscribe(OnSettingsChanged);
        _workspaceSubscription = context.Workspace.Subscribe(
            new PaperTodoEventFilter
            {
                Kinds = new HashSet<PaperTodoEventKind>
                {
                    PaperTodoEventKind.PaperCreated,
                    PaperTodoEventKind.PaperChanged,
                    PaperTodoEventKind.PaperDeleted,
                    PaperTodoEventKind.TodoCreated,
                    PaperTodoEventKind.TodoChanged,
                    PaperTodoEventKind.TodoDeleted,
                    PaperTodoEventKind.NoteChanged
                },
                ExcludeOwnOperations = true
            },
            OnWorkspaceEvent);

        // Startup pull check after short delay
        Task.Delay(1500).ContinueWith(_ =>
        {
            if (!_disposed && _settings.ToS3Config().IsValid)
            {
                _ = TriggerSyncAsync(manual: false);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _debounceTimer.Dispose();
        _settingsSubscription.Dispose();
        _workspaceSubscription.Dispose();

        _context.GlobalTopBar.SetActionHandler(null);
        _context.GlobalTopBar.Clear();
        MinioSyncPlugin.SyncTrigger = null;

        // If there are pending changes on exit, attempt a final quick sync
        if (_hasPendingChanges && _settings.ToS3Config().IsValid)
        {
            try
            {
                TriggerSyncAsync(manual: false).Wait(TimeSpan.FromSeconds(3));
            }
            catch { }
        }

        _syncLock.Dispose();
    }

    private void OnTopBarAction(PaperTopBarActionInvocation invocation)
    {
        if (invocation.ActionId == ActionIdSyncNow)
        {
            _ = TriggerSyncAsync(manual: true);
        }
    }

    private void OnSettingsChanged(string json)
    {
        if (_disposed) return;
        _settings = MinioPluginSettings.FromJson(json);
        if (_settings.ToS3Config().IsValid)
        {
            _ = TriggerSyncAsync(manual: true);
        }
    }

    private void OnWorkspaceEvent(PaperTodoEvent evt)
    {
        if (_disposed || !_settings.AutoSync) return;

        _hasPendingChanges = true;
        // Reset debounce timer to 30 seconds
        _debounceTimer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
    }

    private void OnDebounceElapsed(object? state)
    {
        if (_disposed || !_hasPendingChanges) return;
        _ = TriggerSyncAsync(manual: false);
    }

    public async Task<SyncResult> TriggerSyncAsync(bool manual)
    {
        var config = _settings.ToS3Config();
        if (!config.IsValid)
        {
            UpdateTopBarStatus(isSyncing: false, statusText: "MinIO 配置不完整，请在设置中填写", iconKind: "warning");
            return new SyncResult { Success = false, ErrorMessage = "MinIO config is invalid" };
        }

        if (!await _syncLock.WaitAsync(0).ConfigureAwait(false))
        {
            // Already syncing
            return new SyncResult { Success = false, ErrorMessage = "Already syncing" };
        }

        UpdateTopBarStatus(isSyncing: true, statusText: "MinIO 正在同步...");

        try
        {
            using var client = new S3Client(config);
            var engine = new SyncEngine(client, config.NormalizedPrefix, _state.DeviceId, Environment.MachineName);

            var hashes = new Dictionary<string, string>(_state.KnownLocalHashes, StringComparer.Ordinal);
            var noteAssets = _context.NoteAssets;

            var result = await engine.ExecuteSyncAsync(
                _context.Workspace,
                noteAssets,
                _state.LastKnownRevision,
                hashes).ConfigureAwait(false);

            if (result.Success)
            {
                _hasPendingChanges = false;
                _state = _state with
                {
                    LastSyncTime = DateTimeOffset.UtcNow,
                    KnownLocalHashes = hashes,
                    LastKnownRevision = result.NewRevision > 0 ? result.NewRevision : _state.LastKnownRevision
                };
                _context.State.Save(_state.ToJson());

                var details = $"已同步 (推:{result.PushedCount} 拉:{result.PulledCount})";
                if (result.ConflictCount > 0)
                {
                    UpdateTopBarStatus(
                        isSyncing: false,
                        statusText: $"MinIO 存在 {result.ConflictCount} 个冲突副本，请核对便签！",
                        iconKind: "conflict");
                }
                else
                {
                    UpdateTopBarStatus(
                        isSyncing: false,
                        statusText: $"MinIO 同步成功 {DateTime.Now:HH:mm:ss} [{details}]",
                        iconKind: "success");
                }
            }
            else
            {
                UpdateTopBarStatus(
                    isSyncing: false,
                    statusText: $"MinIO 同步失败: {result.ErrorMessage}",
                    iconKind: "error");
            }

            return result;
        }
        catch (Exception ex)
        {
            UpdateTopBarStatus(
                isSyncing: false,
                statusText: $"MinIO 同步异常: {ex.Message}",
                iconKind: "error");
            return new SyncResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private void UpdateTopBarStatus(bool isSyncing, string statusText, string iconKind = "normal")
    {
        var iconChar = isSyncing ? "⏳" : iconKind switch
        {
            "success" => "☁",
            "conflict" => "⚠",
            "warning" => "❓",
            "error" => "❌",
            _ => "☁"
        };

        _context.GlobalTopBar.SetActions([
            new PaperTopBarAction
            {
                Id = ActionIdSyncNow,
                Icon = PaperTopBarIcon.Character(iconChar),
                ToolTip = statusText,
                Priority = 90
            }
        ]);
    }
}
