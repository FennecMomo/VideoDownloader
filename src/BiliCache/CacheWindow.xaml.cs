using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace BiliCache;

public partial class CacheWindow : Window
{
    private static readonly int[] RetryDelays = [3, 10, 30];
    public ObservableCollection<CacheJob> Jobs { get; } = [];
    private readonly Storage storage = new();
    private readonly DownloadService service = new();
    private Settings settings = new();
    private VideoInfo? video;
    private CancellationTokenSource? analyzeCancellation, downloadCancellation;
    private readonly CancellationTokenSource accountCancellation = new();
    private CacheJob? currentJob;
    private bool running, shuttingDown, allowClose;
    private Task? queueTask, analysisTask, accountTask;

    public CacheWindow() { InitializeComponent(); DataContext = this; }
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            settings = storage.Load("settings.json", new Settings());
            DownloadFolderBox.Text = settings.DownloadDirectory; CookieBox.Text = settings.CookieFile; ToolsBox.Text = settings.ToolsDirectory;
            try
            {
                if (settings.CookieFile.Length == 0 && new AccountSession().Load() is { } savedAccount)
                {
                    settings.UseAccountSession = true; settings.AccountName = savedAccount.Account.Name;
                }
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            RefreshAccountStatus();
            foreach (var job in storage.Load("jobs.json", new List<CacheJob>()))
            {
                if (job.State is "缓存中" or "等待重试" or "待续传" or "已中断")
                {
                    job.State = "等待中"; job.Detail = "等待自动续传";
                }
                if (job.State == "已完成" && !File.Exists(job.FilePath)) { job.State = "文件丢失"; job.Detail = "本地文件已移动或删除，可重新缓存"; }
                Jobs.Add(job);
            }
            if (CheckTools()) StartQueue();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private bool CheckTools(TextBlock? feedback = null)
    {
        try { DownloadService.ValidateTools(settings); EngineStatus.Text = "● 下载组件已就绪"; return true; }
        catch (Exception ex) { EngineStatus.Text = "● 请配置下载组件"; (feedback ?? StatusText).Text = ex.Message; return false; }
    }
    private void UrlBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        video = null;
        if (AddButton != null) AddButton.IsEnabled = false;
        if (QualityBox != null) QualityBox.IsEnabled = false;
    }
    private async void Analyze_Click(object sender, RoutedEventArgs e) { analysisTask = AnalyzeAsync(); await analysisTask; }
    private async Task AnalyzeAsync()
    {
        analyzeCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        AnalyzeButton.IsEnabled = false; UrlBox.IsEnabled = false; AddButton.IsEnabled = false;
        CancelAnalyzeButton.Visibility = Visibility.Visible; StatusText.Text = "正在加载视频页面，获取可用清晰度（首次可能需要数十秒）…";
        try
        {
            video = await service.AnalyzeAsync(UrlBox.Text, settings, analyzeCancellation.Token);
            VideoTitle.Text = video.Title;
            var duration = TimeSpan.FromSeconds(video.Duration);
            VideoMeta.Text = $"{video.Author} · 时长 {duration:hh\\:mm\\:ss} · 当前链接对应的视频 / 分P";
            var options = new List<QualityOption> { new(0, "最高可用清晰度") };
            options.AddRange(video.Heights.Select(h => new QualityOption(h, $"最高 {h}p")));
            QualityBox.ItemsSource = options; QualityBox.SelectedIndex = 0; QualityBox.IsEnabled = true; AddButton.IsEnabled = true;
            StatusText.Text = "解析成功。清晰度取决于视频源和当前登录权限。";
        }
        catch (OperationCanceledException) { StatusText.Text = "解析已取消或超时，可重试。"; }
        catch (LoginExpiredException ex) { video = null; AccountStatus.Text = "登录已失效 · 请重新登录"; StatusText.Text = ex.Message; }
        catch (Exception ex) { video = null; ShowError(ex); }
        finally
        {
            analyzeCancellation.Dispose(); analyzeCancellation = null; AnalyzeButton.IsEnabled = true; UrlBox.IsEnabled = true;
            CancelAnalyzeButton.Visibility = Visibility.Collapsed;
        }
    }
    private void CancelAnalyze_Click(object sender, RoutedEventArgs e) => analyzeCancellation?.Cancel();
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (video == null || QualityBox.SelectedItem is not QualityOption quality) return;
        if (Jobs.Any(j => j.Url == video.Url && j.Height == quality.Height && j.State is "等待中" or "缓存中" or "等待重试" or "待续传")) { StatusText.Text = "相同的视频和清晰度已在队列中。"; return; }
        var job = new CacheJob { Url = video.Url, Title = video.Title, Height = quality.Height };
        job.Folder = Path.Combine(settings.DownloadDirectory, job.Id);
        Jobs.Insert(0, job); JobsList.SelectedItem = job; Persist(); StartQueue();
    }
    private void StartQueue() { if (!running && !shuttingDown) queueTask = RunQueueAsync(); }
    private async Task RunQueueAsync()
    {
        running = true;
        try
        {
            while (!shuttingDown && Jobs.LastOrDefault(j => j.State == "等待中") is { } job)
            {
                currentJob = job;
                using var cancellation = new CancellationTokenSource();
                downloadCancellation = cancellation;
                try
                {
                    var snapshot = new Settings { DownloadDirectory = settings.DownloadDirectory, CookieFile = settings.CookieFile, ToolsDirectory = settings.ToolsDirectory, UseAccountSession = settings.UseAccountSession };
                    string path = await DownloadWithRetryAsync(job, snapshot, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    job.FilePath = path;
                    job.State = "已完成"; job.Progress = 100; job.Detail = $"已缓存 · {new FileInfo(job.FilePath).Length / 1048576d:F1} MB · 可离线播放";
                    StatusText.Text = $"缓存完成：{job.Title}";
                }
                catch (Exception) when (shuttingDown) { }
                catch (Exception) when (cancellation.IsCancellationRequested) { job.State = "已取消"; job.Detail = DownloadService.CancellationDetail(job.Folder); }
                catch (DownloadInterruptedException ex)
                {
                    job.State = "待续传";
                    job.Detail = $"自动重试已用完。可手动继续，或下次启动时自动续传。\n{ex.Message}";
                    StatusText.Text = "缓存因网络问题中断，已保留下载数据。";
                }
                catch (OperationCanceledException) { job.State = "失败"; job.Detail = "网络请求超时，未完成缓存。请检查网络或代理设置后重试。"; StatusText.Text = job.Detail; }
                catch (LoginExpiredException ex) { job.State = "失败"; job.Detail = ex.Message; AccountStatus.Text = "登录已失效 · 请重新登录"; StatusText.Text = ex.Message; }
                catch (Exception ex) { job.State = "失败"; job.Detail = ex.Message; StatusText.Text = "缓存失败。选中任务后可重试，悬停说明可查看详情。"; }
                finally { downloadCancellation = null; currentJob = null; if (!shuttingDown) Persist(); }
            }
        }
        finally { running = false; }
    }

    private async Task<string> DownloadWithRetryAsync(CacheJob job, Settings snapshot, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            job.State = "缓存中"; job.Detail = "正在连接 B 站…"; Persist();
            var progress = new Progress<DownloadUpdate>(update =>
            {
                if (shuttingDown || job.State != "缓存中") return;
                job.Progress = update.Percent; job.Detail = update.Detail;
            });
            try { return await service.DownloadAsync(job, snapshot, progress, cancellationToken); }
            catch (DownloadInterruptedException ex) when (attempt < RetryDelays.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int seconds = RetryDelays[attempt];
                job.State = "等待重试";
                job.Detail = $"{seconds} 秒后自动续传（{attempt + 1}/{RetryDelays.Length}）。\n{ex.Message}";
                Persist();
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
        }
    }
    private CacheJob? Selected() => JobsList.SelectedItem as CacheJob;
    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } job) { StatusText.Text = "请先选择一个缓存任务。"; return; }
        if (job == currentJob || job.State is "缓存中" or "等待中" or "等待重试" or "已完成") { StatusText.Text = "此任务无需重试。"; return; }
        job.State = "等待中"; job.Detail = "等待继续缓存"; Persist(); StartQueue();
    }
    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } job) return;
        if (job == currentJob)
        {
            job.State = "已取消"; job.Detail = "正在停止缓存，可手动继续。"; Persist();
            _ = downloadCancellation?.CancelAsync();
        }
        else if (job.State is "等待中" or "待续传")
        {
            job.State = "已取消"; job.Detail = "任务已取消，可手动继续。"; Persist();
        }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { } job) return;
        if (job == currentJob) { StatusText.Text = "请先取消正在缓存的任务。"; return; }
        Jobs.Remove(job); Persist(); StatusText.Text = "已移除记录，磁盘上的视频和下载片段仍然保留。";
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not { State: "已完成" } job) { StatusText.Text = "请先选中一个已完成的缓存。"; return; }
        try
        {
            if (!File.Exists(job.FilePath)) { job.State = "文件丢失"; Persist(); throw new FileNotFoundException("缓存文件已移动或删除，可重新缓存。"); }
            MainTabs.SelectedItem = PlayerTab;
            await Player.OpenAsync(job.FilePath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
    }
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs || MainTabs.SelectedItem == PlayerTab || Player is not { } player) return;
        player.EndKeyboardSeek();
        if (player.IsPlaying) player.Pause();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled && MainTabs?.SelectedItem == PlayerTab && Player is { } player &&
            (e.OriginalSource is not TabItem tab || tab == PlayerTab) && player.HandlePlayerKeyDown(e))
            e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (Player?.HandleSeekKeyUp(e) == true) e.Handled = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        Player?.EndKeyboardSeek();
        base.OnDeactivated(e);
    }
    private void RefreshAccountStatus() => AccountStatus.Text = !string.IsNullOrWhiteSpace(settings.CookieFile) ? "使用导入的 Cookie 文件" : settings.UseAccountSession ? $"已保存账号：{settings.AccountName} · 解析时验证有效性" : "未登录 · 使用公开清晰度";
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (running || analyzeCancellation != null) { SettingsStatusText.Text = "请等当前解析或缓存结束后再登录或切换账号。"; return; }
        var window = new AccountWindow { Owner = this, Icon = Icon };
        if (window.ShowDialog() == true && window.Account is { } account)
        {
            try
            {
                settings.UseAccountSession = true; settings.AccountName = account.Name;
                settings.CookieFile = ""; CookieBox.Text = "";
                storage.Save("settings.json", settings);
                service.ClearResolved(); video = null; AddButton.IsEnabled = false; QualityBox.IsEnabled = false;
                AccountStatus.Text = $"已登录：{account.Name} · 已自动保存";
                SettingsStatusText.Text = "账号已保存，后续自动使用此账号获取视频资源。";
                if (!string.IsNullOrWhiteSpace(UrlBox.Text)) { analysisTask = AnalyzeAsync(); await analysisTask; }
            }
            catch (Exception ex) { ShowError(ex, SettingsStatusText); }
        }
    }
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (running || analyzeCancellation != null) { SettingsStatusText.Text = "请等当前解析或缓存结束后再退出账号。"; return; }
        accountTask = LogoutAsync();
        await accountTask;
    }
    private async Task LogoutAsync()
    {
        IsEnabled = false;
        try
        {
            await AccountWindow.ClearSessionAsync(accountCancellation.Token);
            settings.UseAccountSession = false; settings.AccountName = ""; settings.CookieFile = ""; CookieBox.Text = "";
            storage.Save("settings.json", settings); service.ClearResolved();
            video = null; AddButton.IsEnabled = false; QualityBox.IsEnabled = false;
            RefreshAccountStatus(); SettingsStatusText.Text = "工具内的登录会话已清除，缓存文件保留。";
        }
        catch (OperationCanceledException) when (accountCancellation.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex, SettingsStatusText); }
        finally { if (!shuttingDown) IsEnabled = true; }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Selected()?.Folder ?? settings.DownloadDirectory; Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void BrowseDownloads_Click(object sender, RoutedEventArgs e) => PickFolder(DownloadFolderBox);
    private void BrowseTools_Click(object sender, RoutedEventArgs e) => PickFolder(ToolsBox);
    private void PickFolder(System.Windows.Controls.TextBox box) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName; }
    private void BrowseCookies_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "Cookie 文本文件|*.txt|所有文件|*.*" }; if (dialog.ShowDialog(this) == true) CookieBox.Text = dialog.FileName; }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = DownloadFolderBox.Text.Trim();
            if (!Path.IsPathFullyQualified(folder)) throw new ArgumentException("请选择完整的缓存目录路径。");
            Directory.CreateDirectory(folder);
            var updated = new Settings { DownloadDirectory = Path.GetFullPath(folder), CookieFile = CookieBox.Text.Trim(), ToolsDirectory = ToolsBox.Text.Trim(), UseAccountSession = settings.UseAccountSession, AccountName = settings.AccountName };
            if (updated.CookieFile.Length > 0 && !File.Exists(updated.CookieFile)) throw new FileNotFoundException("所选 Cookie 文件不存在。");
            storage.Save("settings.json", updated); settings = updated; SettingsStatusText.Text = "设置已保存，对后续解析和缓存生效。";
            service.ClearResolved();
            RefreshAccountStatus();
            video = null; AddButton.IsEnabled = false; QualityBox.IsEnabled = false;
            if (CheckTools(SettingsStatusText)) StartQueue();
        }
        catch (Exception ex) { ShowError(ex, SettingsStatusText); }
    }
    private void Persist() { try { storage.Save("jobs.json", Jobs.ToList()); } catch (Exception ex) { StatusText.Text = "任务历史保存失败：" + ex.Message; } }
    private void ShowError(Exception ex, TextBlock? feedback = null)
    {
        (feedback ?? StatusText).Text = ex.Message.Split('\n')[0];
        if (!shuttingDown) MessageBox.Show(this, ex.Message, "BiliCache", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        if (shuttingDown) { e.Cancel = true; return; }
        shuttingDown = true;
        Task[] active = new[] { analysisTask, queueTask, accountTask }.OfType<Task>().Where(task => !task.IsCompleted).ToArray();
        if (active.Length == 0)
        {
            accountCancellation.Dispose();
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        // Resume states are already on disk. Yield before attempting a second Close, even if cancellation completes inline.
        Task cleanup = Task.WhenAll(active.Append(analyzeCancellation?.CancelAsync() ?? Task.CompletedTask)
            .Append(downloadCancellation?.CancelAsync() ?? Task.CompletedTask).Append(accountCancellation.CancelAsync()));
        await System.Windows.Threading.Dispatcher.Yield();
        try
        {
            Hide();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            if (cleanup.IsCompleted) accountCancellation.Dispose();
            allowClose = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Player.Dispose();
        base.OnClosed(e);
    }
}
