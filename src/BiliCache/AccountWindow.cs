using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BiliCache;

/// <summary>The user signs in directly on Bilibili's official page. The app never handles passwords.</summary>
public sealed class AccountWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;
    private string? navigationError;
    private readonly TextBlock status = new() { Text = "请在 B站官方页面自行登录。确认成功后自动保存账号并返回工具。", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    public AccountIdentity? Account { get; private set; }
    public AccountWindow()
    {
        Title = "BiliCache · B站官方登录"; Width = 1050; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel();
        var bar = new DockPanel();
        var done = new Button { Content = "取消", Margin = new Thickness(10), Padding = new Thickness(15, 8, 15, 8) };
        done.Click += (_, _) => { DialogResult = false; };
        var reload = new Button { Content = "重新加载", Margin = new Thickness(10), Padding = new Thickness(15, 8, 15, 8) };
        reload.Click += (_, _) => { if (browser.CoreWebView2 != null) { navigationError = null; status.Text = "正在重新加载登录页面…"; browser.CoreWebView2.Navigate("https://passport.bilibili.com/login"); } };
        DockPanel.SetDock(done, Dock.Right); bar.Children.Add(done);
        DockPanel.SetDock(reload, Dock.Right); bar.Children.Add(reload); bar.Children.Add(status);
        DockPanel.SetDock(bar, Dock.Top); panel.Children.Add(bar); panel.Children.Add(browser); Content = panel;
        Loaded += InitializeAsync;
        Closed += (_, _) => { closed = true; lifetime.Cancel(); browser.Dispose(); };
    }
    private async void InitializeAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: BrowserResolver.ProfilePath("", true));
            lifetime.Token.ThrowIfCancellationRequested();
            await browser.EnsureCoreWebView2Async(environment);
            lifetime.Token.ThrowIfCancellationRequested();
            var session = new AccountSession();
            await session.RestoreAsync(browser.CoreWebView2);
            browser.CoreWebView2.IsMuted = true;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            browser.CoreWebView2.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
            browser.CoreWebView2.NavigationStarting += (_, args) => { if (!IsBiliPage(args.Uri)) args.Cancel = true; };
            browser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                navigationError = args.IsSuccess ? null : $"登录页面加载失败（{args.WebErrorStatus}）。请检查网络或代理设置，然后点击“重新加载”。";
                if (navigationError != null) status.Text = navigationError;
            };
            browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (IsBiliPage(args.Uri)) browser.CoreWebView2.Navigate(args.Uri);
            };
            browser.CoreWebView2.Navigate("https://passport.bilibili.com/login");
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    var cookies = await AccountSession.CaptureAsync(browser.CoreWebView2);
                    var account = await AccountSession.VerifyAsync(cookies, browser.CoreWebView2.Settings.UserAgent, lifetime.Token);
                    if (account != null)
                    {
                        session.Save(new SavedSession(account, cookies));
                        Account = account;
                        DialogResult = true;
                        return;
                    }
                    status.Text = navigationError ?? "等待登录：在官方页面扫码或登录，成功后会自动保存并返回。";
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception ex) when (!closed)
                {
                    status.Text = navigationError ?? (ex is TimeoutException or System.Net.Http.HttpRequestException ? ex.Message : "暂时无法确认或保存账号，稍后自动重试。请保持窗口打开。");
                    await Task.Delay(7000, lifetime.Token);
                }
                await Task.Delay(3000, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) status.Text = "登录页面未能打开：" + ex.Message; }
    }
    private static bool IsBiliPage(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && (uri.Host == "bilibili.com" || uri.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase));

    public static async Task ClearSessionAsync()
    {
        var browser = new WebView2();
        var host = new Window { Content = browser, Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            host.Show();
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: BrowserResolver.ProfilePath("", true));
            await browser.EnsureCoreWebView2Async(environment);
            await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            if ((await AccountSession.CaptureAsync(browser.CoreWebView2)).Any(c => c.Name == "SESSDATA")) throw new InvalidOperationException("登录会话仍在使用，请稍后重试退出。");
            new AccountSession().Forget();
        }
        finally { browser.Dispose(); host.Close(); }
    }
}
