using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo.Plugin;

namespace PaperTodo.Plugin.MinioSync;

public sealed class MinioSyncPlugin : IPaperBodyPlugin, IPaperPluginRuntimeProvider
{
    internal static Func<bool, Task<Engine.SyncResult>>? SyncTrigger { get; set; }

    public IPaperBodySession Create(PaperBodyContext context) =>
        new MinioSyncSession(context);

    public IPaperPluginRuntime CreatePluginRuntime(PaperPluginRuntimeContext context) =>
        new MinioSyncRuntime(context);

    private sealed class MinioSyncSession : IPaperBodySession
    {
        private readonly Grid _view;
        private readonly TextBlock _statusText;
        private readonly Button _syncButton;
        private readonly PaperBodyContext _context;

        public FrameworkElement View => _view;

        public MinioSyncSession(PaperBodyContext context)
        {
            _context = context;

            _view = new Grid
            {
                Margin = new Thickness(16)
            };
            _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _view.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _view.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _statusText = new TextBlock
            {
                Text = "MinIO 串行接力同步正在后台运行。\n所有纸片顶栏已添加快捷同步图标。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
                FontSize = 14
            };
            Grid.SetRow(_statusText, 0);

            _syncButton = new Button
            {
                Content = "立即同步 (Sync Now)",
                Padding = new Thickness(16, 8, 16, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _syncButton.Click += async (_, _) =>
            {
                if (SyncTrigger != null)
                {
                    _statusText.Text = "正在执行同步，请稍候...";
                    try
                    {
                        var res = await SyncTrigger(true).ConfigureAwait(true);
                        _statusText.Text = res.Success
                            ? $"同步成功 (推:{res.PushedCount} 拉:{res.PulledCount})，顶栏状态已更新。"
                            : $"同步失败: {res.ErrorMessage}";
                    }
                    catch (Exception ex)
                    {
                        _statusText.Text = $"同步异常: {ex.Message}";
                    }
                }
                else
                {
                    _statusText.Text = "同步后台服务未就绪，请在设置中开启并填写配置。";
                }
            };
            Grid.SetRow(_syncButton, 1);

            _view.Children.Add(_statusText);
            _view.Children.Add(_syncButton);
        }

        public void OnThemeChanged(PaperBodyTheme theme)
        {
            if (!string.IsNullOrWhiteSpace(theme.TextColor))
            {
                _statusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.TextColor));
            }
        }

        public void Dispose()
        {
        }
    }
}
