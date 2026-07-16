using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GreenAppleDownloader.Models;
using GreenAppleDownloader.Services;
using Microsoft.Win32;

namespace GreenAppleDownloader;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DownloadTaskItem> _tasks = [];
    private readonly SettingsService _settingsService = new();
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _appInstance = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private AppSettings _settings;
    private GoogleOAuthService _oauth = null!;
    private SheetsQueueService _queue = null!;
    private DriveDownloadService _drive = null!;
    private CancellationTokenSource? _downloadCts;
    private bool _isDownloading;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsService.Load();
        var tokenStore = new SecureTokenStore(_settingsService.AppDataDirectory);
        _oauth = new GoogleOAuthService(_httpClient, tokenStore);
        var api = new GoogleApiClient(_httpClient, _oauth, () => _settings.ClientSecretsPath);
        _queue = new SheetsQueueService(api);
        _drive = new DriveDownloadService(api);
        TaskGrid.ItemsSource = _tasks;
        LoadSettingsIntoControls();
        RefreshSummary();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateConnectionUi();
        _ = PollLoopAsync(_lifetimeCts.Token);
        if (_settings.AutoSync && IsConfigured())
        {
            await SyncAndDownloadAsync(showEmptyMessage: false);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _lifetimeCts.Cancel();
        _downloadCts?.Cancel();
        _httpClient.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_settings.PollSeconds, 3, 60)), cancellationToken);
                if (_settings.AutoSync && IsConfigured() && !_isDownloading)
                {
                    await Dispatcher.InvokeAsync(() => SyncAndDownloadAsync(showEmptyMessage: false));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // The next polling interval will try again; manual sync shows detailed errors.
            }
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e) => await SyncAndDownloadAsync(showEmptyMessage: true);

    private async Task SyncAndDownloadAsync(bool showEmptyMessage)
    {
        if (!_syncLock.Wait(0))
        {
            return;
        }

        try
        {
            SaveControlsToSettings(showConfirmation: false);
            if (!IsConfigured())
            {
                ShowSettingsView();
                SetTopStatus("请先完成 Google 设置", warning: true);
                if (showEmptyMessage)
                {
                    MessageBox.Show("请先选择 OAuth JSON、填写表格链接并连接 Google 账号。", "需要完成设置", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            SyncButton.IsEnabled = false;
            SetTopStatus("正在检查表格…");
            QueueHintText.Text = $"{DateTime.Now:HH:mm:ss} 正在连接下载队列…";
            await _queue.EnsureQueueSheetAsync(_settings.SpreadsheetId, _lifetimeCts.Token);
            var pending = await _queue.GetPendingAsync(_settings.SpreadsheetId, _lifetimeCts.Token);
            var newItems = new List<DownloadTaskItem>();
            foreach (var item in pending)
            {
                var existing = _tasks.FirstOrDefault(current => current.TaskId == item.TaskId);
                if (existing is null)
                {
                    _tasks.Add(item);
                    newItems.Add(item);
                }
                else if (existing.Status is "下载失败" or "已停止")
                {
                    existing.Status = "等待下载";
                    existing.Progress = 0;
                    existing.Error = string.Empty;
                    newItems.Add(existing);
                }
            }

            RefreshSummary();
            if (newItems.Count == 0)
            {
                SetTopStatus("暂无新任务");
                QueueHintText.Text = $"最后检查 {DateTime.Now:HH:mm:ss}：连接正常，暂时没有新的 PENDING 任务。";
                return;
            }

            await DownloadBatchAsync(newItems);
        }
        catch (OperationCanceledException)
        {
            SetTopStatus("已停止", warning: true);
        }
        catch (Exception ex)
        {
            SetTopStatus("检查失败", warning: true);
            QueueHintText.Text = $"最后检查 {DateTime.Now:HH:mm:ss} 失败：{FriendlyError(ex).Replace(Environment.NewLine, " ")}";
            if (showEmptyMessage)
            {
                MessageBox.Show(FriendlyError(ex), "未能读取下载队列", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            SyncButton.IsEnabled = true;
            _syncLock.Release();
        }
    }

    private async void ManualDownload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveControlsToSettings(showConfirmation: false);
            if (!_oauth.HasStoredToken || string.IsNullOrWhiteSpace(_settings.ClientSecretsPath))
            {
                ShowSettingsView();
                MessageBox.Show("下载私有 Drive 文件前，请先连接 Google 账号。", "需要连接 Google", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var batchId = Guid.NewGuid().ToString("N");
            var links = ClipboardLinkExtractor.Extract(ManualLinksTextBox.Text, html: null, rtf: null);
            var items = new List<DownloadTaskItem>();
            foreach (var link in links)
            {
                if (!DriveLinkParser.TryExtractFileId(link, out var fileId) || items.Any(item => item.FileId == fileId))
                {
                    continue;
                }

                var item = new DownloadTaskItem
                {
                    BatchId = batchId,
                    DriveUrl = link,
                    FileId = fileId,
                    DisplayText = link,
                    SourceSheet = "手动粘贴",
                    SourceCell = "—"
                };
                items.Add(item);
                _tasks.Add(item);
            }

            if (items.Count == 0)
            {
                MessageBox.Show(
                    "没有识别到有效的 Google Drive 文件链接。\n\n如果单元格显示的是文字超链接，请重新复制单元格，然后在这里按 Ctrl+V，或点击“从剪贴板提取超链接”。",
                    "没有可下载链接",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ShowHomeView();
            await DownloadBatchAsync(items);
        }
        catch (Exception ex)
        {
            MessageBox.Show(FriendlyError(ex), "下载未开始", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task DownloadBatchAsync(IReadOnlyList<DownloadTaskItem> items)
    {
        if (_isDownloading || items.Count == 0)
        {
            return;
        }

        _isDownloading = true;
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        CancelButton.Visibility = Visibility.Visible;
        SyncButton.IsEnabled = false;
        ManualDownloadButton.IsEnabled = false;
        var token = _downloadCts.Token;
        var label = FileNameHelper.Sanitize(items[0].SourceSheet, "成品");
        var folderName = $"成品下载_{DateTime.Now:yyyyMMdd_HHmmss}_{label}";
        var batchDirectory = FileNameHelper.EnsureUniqueDirectory(_settings.DownloadRoot, folderName);
        QueueHintText.Text = $"本批 {items.Count} 个文件，正在保存到 {batchDirectory}";
        SetTopStatus($"正在下载 0/{items.Count}");

        try
        {
            using var gate = new SemaphoreSlim(Math.Clamp(_settings.Concurrency, 1, 5));
            var completed = 0;
            var work = items.Select(async item =>
            {
                await gate.WaitAsync(token);
                try
                {
                    item.Status = "正在下载";
                    if (item.QueueRow > 0)
                    {
                        await TryUpdateQueueAsync(item, token);
                    }

                    var progress = new Progress<DownloadProgress>(update =>
                    {
                        update.Task.Status = update.Status;
                        update.Task.Progress = update.Percent;
                        if (!string.IsNullOrWhiteSpace(update.FileName)) update.Task.FileName = update.FileName;
                        if (!string.IsNullOrWhiteSpace(update.LocalPath)) update.Task.LocalPath = update.LocalPath;
                        RefreshSummary();
                    });

                    item.LocalPath = await _drive.DownloadAsync(item, batchDirectory, _settings.MaxRetries, progress, token);
                    item.Status = "已完成";
                    item.Progress = 100;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "已停止";
                    item.Error = "用户停止了本批下载；下次可以重新发送此链接。";
                }
                catch (Exception ex)
                {
                    item.Status = "下载失败";
                    item.Error = FriendlyError(ex);
                }
                finally
                {
                    if (item.QueueRow > 0)
                    {
                        await TryUpdateQueueAsync(item, CancellationToken.None);
                    }

                    var nowDone = Interlocked.Increment(ref completed);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SetTopStatus($"正在下载 {nowDone}/{items.Count}");
                        RefreshSummary();
                    });
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(work);

            var successes = items.Count(item => item.Status == "已完成");
            if (_settings.ZipAfterDownload && successes > 0)
            {
                SetTopStatus("正在创建 ZIP…");
                var zipPath = Path.Combine(_settings.DownloadRoot, Path.GetFileName(batchDirectory) + ".zip");
                await Task.Run(() => ZipFile.CreateFromDirectory(batchDirectory, zipPath, CompressionLevel.Fastest, includeBaseDirectory: false), token);
            }

            SetTopStatus(successes == items.Count ? $"本批 {successes} 个全部完成" : $"完成 {successes} 个，失败 {items.Count - successes} 个", warning: successes != items.Count);
            QueueHintText.Text = _settings.ZipAfterDownload
                ? "下载完成；原视频与额外生成的 ZIP 都已保留。"
                : "下载完成；ZIP 未开启，因此无需等待压缩。";
            if (_settings.OpenFolderWhenComplete && Directory.Exists(batchDirectory))
            {
                OpenPath(batchDirectory);
            }
        }
        finally
        {
            _isDownloading = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            CancelButton.Visibility = Visibility.Collapsed;
            SyncButton.IsEnabled = true;
            ManualDownloadButton.IsEnabled = true;
            RefreshSummary();
        }
    }

    private async Task TryUpdateQueueAsync(DownloadTaskItem item, CancellationToken token)
    {
        try
        {
            await _queue.UpdateTaskAsync(_settings.SpreadsheetId, item, _appInstance, token);
        }
        catch (Exception ex)
        {
            if (string.IsNullOrWhiteSpace(item.Error))
            {
                item.Error = "本地任务已执行，但表格状态回写失败：" + FriendlyError(ex);
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveControlsToSettings(showConfirmation: true);

    private void SaveControlsToSettings(bool showConfirmation)
    {
        _settings.ClientSecretsPath = ClientSecretsPathTextBox.Text.Trim();
        var sheetInput = SpreadsheetTextBox.Text.Trim();
        _settings.SpreadsheetId = DriveLinkParser.ExtractSpreadsheetId(sheetInput) ?? sheetInput;
        _settings.DownloadRoot = string.IsNullOrWhiteSpace(DownloadRootTextBox.Text)
            ? new AppSettings().DownloadRoot
            : DownloadRootTextBox.Text.Trim();
        _settings.Concurrency = ComboInt(ConcurrencyComboBox, 2);
        _settings.MaxRetries = ComboInt(RetryComboBox, 5);
        _settings.AutoSync = AutoSyncCheckBox.IsChecked == true;
        _settings.OpenFolderWhenComplete = OpenFolderCheckBox.IsChecked == true;
        _settings.ZipAfterDownload = ZipCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
        ConcurrencySummaryText.Text = $"{_settings.Concurrency} 个";
        if (showConfirmation)
        {
            SettingsSavedText.Text = "已保存";
            SetTopStatus("设置已保存");
        }
        UpdateConnectionUi();
    }

    private void LoadSettingsIntoControls()
    {
        ClientSecretsPathTextBox.Text = _settings.ClientSecretsPath;
        SpreadsheetTextBox.Text = _settings.SpreadsheetId;
        DownloadRootTextBox.Text = _settings.DownloadRoot;
        SelectCombo(ConcurrencyComboBox, _settings.Concurrency);
        SelectCombo(RetryComboBox, _settings.MaxRetries);
        AutoSyncCheckBox.IsChecked = _settings.AutoSync;
        OpenFolderCheckBox.IsChecked = _settings.OpenFolderWhenComplete;
        ZipCheckBox.IsChecked = _settings.ZipAfterDownload;
        ConcurrencySummaryText.Text = $"{_settings.Concurrency} 个";
    }

    private async void ConnectGoogle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveControlsToSettings(showConfirmation: false);
            ConnectGoogleButton.IsEnabled = false;
            GoogleStatusText.Text = "请在浏览器完成授权…";
            await _oauth.ConnectAsync(_settings.ClientSecretsPath, _lifetimeCts.Token);
            GoogleStatusText.Text = "连接成功";
            UpdateConnectionUi();
            SetTopStatus("Google 已连接");
        }
        catch (Exception ex)
        {
            GoogleStatusText.Text = "连接失败";
            MessageBox.Show(FriendlyError(ex), "Google 连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ConnectGoogleButton.IsEnabled = true;
        }
    }

    private void BrowseSecrets_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Google OAuth 桌面客户端 JSON", Filter = "JSON 文件 (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true)
        {
            ClientSecretsPathTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择青苹果下载目录", InitialDirectory = DownloadRootTextBox.Text };
        if (dialog.ShowDialog(this) == true)
        {
            DownloadRootTextBox.Text = dialog.FolderName;
        }
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_settings.DownloadRoot);
        OpenPath(_settings.DownloadRoot);
    }

    private void OpenSetupGuide_Click(object sender, RoutedEventArgs e)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "配置说明.html"),
            Path.Combine(AppContext.BaseDirectory, "docs", "配置说明.html")
        };
        var guide = candidates.FirstOrDefault(File.Exists);
        if (guide is not null) OpenPath(guide);
        else MessageBox.Show("配置说明文件不在程序目录中。请查看随程序提供的 README。", "找不到说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ManualLinksTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var links = ReadClipboardDriveLinks();
        if (links.Count == 0)
        {
            // Let the TextBox perform its normal plain-text paste when the
            // clipboard does not contain a usable Drive hyperlink.
            return;
        }

        var added = AppendManualLinks(links);
        SetManualClipboardStatus(
            added > 0 ? $"已从剪贴板提取 {added} 个真实链接。" : "剪贴板中的链接已经在列表中。",
            warning: false);
        e.Handled = true;
    }

    private void ReadClipboardLinks_Click(object sender, RoutedEventArgs e)
    {
        var links = ReadClipboardDriveLinks();
        if (links.Count == 0)
        {
            SetManualClipboardStatus("没有读取到链接。请在表格中直接复制目标单元格后立即点击此按钮。", warning: true);
            return;
        }

        var added = AppendManualLinks(links);
        SetManualClipboardStatus(
            added > 0 ? $"已从剪贴板提取 {added} 个真实链接。" : "剪贴板中的链接已经在列表中。",
            warning: false);
    }

    private IReadOnlyList<string> ReadClipboardDriveLinks()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null)
                {
                    return [];
                }

                var html = ClipboardText(data, DataFormats.Html);
                var rtf = ClipboardText(data, DataFormats.Rtf);
                var plainText = ClipboardText(data, DataFormats.UnicodeText) ?? ClipboardText(data, DataFormats.Text);
                return ClipboardLinkExtractor.Extract(plainText, html, rtf);
            }
            catch (COMException) when (attempt < 3)
            {
                Thread.Sleep(35);
            }
        }

        return [];
    }

    private static string? ClipboardText(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format, autoConvert: true))
        {
            return null;
        }

        var value = data.GetData(format, autoConvert: true);
        if (value is string text)
        {
            return text;
        }

        if (value is Stream stream)
        {
            var originalPosition = stream.CanSeek ? stream.Position : 0;
            using var reader = new StreamReader(stream, leaveOpen: true);
            var result = reader.ReadToEnd();
            if (stream.CanSeek) stream.Position = originalPosition;
            return result;
        }

        return value?.ToString();
    }

    private int AppendManualLinks(IEnumerable<string> links)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in ManualLinksTextBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DriveLinkParser.TryExtractFileId(line, out var fileId))
            {
                seenIds.Add(fileId);
            }
        }

        var additions = new List<string>();
        foreach (var link in links)
        {
            if (DriveLinkParser.TryExtractFileId(link, out var fileId) && seenIds.Add(fileId))
            {
                additions.Add(link);
            }
        }

        if (additions.Count == 0)
        {
            return 0;
        }

        var existing = ManualLinksTextBox.Text.TrimEnd();
        ManualLinksTextBox.Text = existing.Length == 0
            ? string.Join(Environment.NewLine, additions)
            : existing + Environment.NewLine + string.Join(Environment.NewLine, additions);
        ManualLinksTextBox.CaretIndex = ManualLinksTextBox.Text.Length;
        ManualLinksTextBox.ScrollToEnd();
        return additions.Count;
    }

    private void SetManualClipboardStatus(string text, bool warning)
    {
        ManualClipboardStatusText.Text = text;
        ManualClipboardStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(warning ? "#B56A15" : "#207A4A"));
    }

    private void ClearManual_Click(object sender, RoutedEventArgs e)
    {
        ManualLinksTextBox.Clear();
        ManualClipboardStatusText.Text = "支持直接网址、文字超链接和文件 ID；复制表格单元格后可直接 Ctrl+V。";
        ManualClipboardStatusText.Foreground = (Brush)FindResource("MutedInkBrush");
    }

    private void ShowHome_Click(object sender, RoutedEventArgs e) => ShowHomeView();
    private void ShowManual_Click(object sender, RoutedEventArgs e) => ShowOnly(ManualView);
    private void ShowSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsView();
    private void ShowHomeView() => ShowOnly(HomeView);
    private void ShowSettingsView() => ShowOnly(SettingsView);

    private void ShowOnly(UIElement view)
    {
        HomeView.Visibility = view == HomeView ? Visibility.Visible : Visibility.Collapsed;
        ManualView.Visibility = view == ManualView ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = view == SettingsView ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsConfigured() =>
        _oauth.HasStoredToken && File.Exists(_settings.ClientSecretsPath) && !string.IsNullOrWhiteSpace(_settings.SpreadsheetId);

    private void UpdateConnectionUi()
    {
        var connected = IsConfigured();
        ConnectionDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(connected ? "#54D482" : "#E6B44A"));
        ConnectionText.Text = connected ? "Google 已连接" : "等待完成设置";
        GoogleStatusText.Text = _oauth.HasStoredToken ? "账号已授权" : "尚未连接";
    }

    private void RefreshSummary()
    {
        TotalCountText.Text = _tasks.Count.ToString();
        DoneCountText.Text = _tasks.Count(task => task.Status == "已完成").ToString();
        EmptyState.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetTopStatus(string text, bool warning = false)
    {
        TopStatusText.Text = text;
        TopStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(warning ? "#B56A15" : "#207A4A"));
    }

    private static int ComboInt(ComboBox combo, int fallback) =>
        combo.SelectedItem is ComboBoxItem item && int.TryParse(item.Content?.ToString(), out var value) ? value : fallback;

    private static void SelectCombo(ComboBox combo, int value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (item.Content?.ToString() == value.ToString())
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static string FriendlyError(Exception error)
    {
        if (error is GoogleApiException api && (int)api.StatusCode == 403)
        {
            return "Google 拒绝了请求。请确认 Drive API 和 Sheets API 已启用，并且当前账号有权查看表格与视频。\n\n" + api.Message;
        }
        return error.Message;
    }
}
