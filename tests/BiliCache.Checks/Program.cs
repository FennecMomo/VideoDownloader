using System.Diagnostics;
using System.IO;
using BiliCache;

static void Assert(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
}

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string output = Path.Combine(root, "test-output");
Directory.CreateDirectory(output);
var settings = new Settings { ToolsDirectory = Path.Combine(root, "tools"), DownloadDirectory = output };
Assert(DownloadService.NormalizeUrl("https://www.bilibili.com/video/BV1cABMYtEj2/?p=2&vd_source=private") == "https://www.bilibili.com/video/BV1cABMYtEj2/?p=2", "preserve page, remove tracking query");
foreach (string bad in new[] { "file:///C:/Windows/win.ini", "https://www.bilibili.com.evil.test/video/BV123", "https://evil.test/", "https://www.bilibili.com:444/video/BV123", "https://www.bilibili.com/bangumi/play/ep123", "https://user@www.bilibili.com/video/BV123" })
{
    bool rejected = false;
    try { DownloadService.NormalizeUrl(bad); } catch (ArgumentException) { rejected = true; }
    Assert(rejected, "reject " + bad);
}
Assert(DownloadService.FriendlyError("HTTP Error 412").Contains("暂时限制"), "actionable site limit error");
DownloadService.ValidateTools(settings);
Assert(true, "all external binaries present");

// Session persistence uses fabricated credentials in an isolated test directory, never a real account.
var testSession = new AccountSession(Path.Combine(output, "session-tests"));
string fakeSecret = "fake-test-session-" + Guid.NewGuid().ToString("N");
var identity = AccountSession.ParseIdentity("{\"code\":0,\"data\":{\"isLogin\":true,\"mid\":123,\"uname\":\"测试账号\"}}");
Assert(identity?.Id == 123 && identity.Name == "测试账号", "account requires server-confirmed identity");
Assert(AccountSession.ParseIdentity("{\"code\":-101}") == null, "expired account is not marked logged in");
Assert(AccountSession.ParseIdentity("{\"code\":0,\"data\":{\"isLogin\":false}}") == null, "anonymous response is not marked logged in");
bool limitedRejected = false;
try { AccountSession.ParseIdentity("{\"code\":-412}"); } catch (InvalidOperationException) { limitedRejected = true; }
Assert(limitedRejected, "transient verification failure is distinct from logout");
var cookie = new SessionCookie("SESSDATA", fakeSecret, ".bilibili.com", "/", true, true, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
Assert(!AccountSession.IsAllowed(cookie with { Domain = ".bilibili.com.attacker.test" }), "reject unrelated cookie domains");
Assert(!AccountSession.IsAllowed(cookie with { Value = "test\r\nInjected: true" }), "reject cookie header injection");
Assert(!AccountSession.IsAllowed(cookie with { Expires = 1 }), "reject expired cookies");
testSession.Save(new SavedSession(identity!, [cookie, cookie with { Domain = ".unrelated.test" }]));
Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(testSession.FilePath)).Contains(fakeSecret), "saved credentials are encrypted, not plaintext");
var restoredSession = new AccountSession(Path.Combine(output, "session-tests")).Load();
Assert(restoredSession?.Cookies.Count == 1 && restoredSession.Cookies[0].Value == fakeSecret && restoredSession.Account == identity, "new session object restores encrypted account and scoped cookies");
testSession.Forget();
Assert(testSession.Load() == null, "forget account removes saved credential");
Assert(await AccountSession.VerifyAsync([], "BiliCache test", default) == null, "missing credentials never claim login");

using (var timeoutClient = new System.Net.Http.HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) })
using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://example.invalid/"))
{
    bool timeout = false;
    try { await NetworkRequest.SendAsync(timeoutClient, request, default); }
    catch (TimeoutException ex) { timeout = ex.Message.Contains("尚未开始下载"); }
    Assert(timeout, "HTTP timeout reports network failure instead of user cancellation");
}
using (var cancelClient = new System.Net.Http.HttpClient(new HangingHandler()))
using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://example.invalid/"))
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
{
    bool canceled = false;
    try { await NetworkRequest.SendAsync(cancelClient, request, cancel.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Assert(canceled, "user cancellation remains distinguishable from HTTP timeout");
}
string partialFolder = Path.Combine(output, "cancel-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(partialFolder);
try
{
    await File.WriteAllTextAsync(Path.Combine(partialFolder, ".download-info.json"), "{}");
    await File.WriteAllBytesAsync(Path.Combine(partialFolder, "empty.mp4.part"), []);
    Assert(DownloadService.CancellationDetail(partialFolder).Contains("未发现"), "metadata and empty partials do not count as downloaded media");
    await File.WriteAllBytesAsync(Path.Combine(partialFolder, "video.mp4.part"), [1, 2]);
    Assert(DownloadService.CancellationDetail(partialFolder).Contains("片段保留"), "nonempty media partials are reported accurately");
}
finally { Directory.Delete(partialFolder, true); }

// Exercise the real media verifier with valid A/V, audio-only, and corrupt files.
async Task Generate(string name, bool audioOnly)
{
    var psi = new ProcessStartInfo(Path.Combine(settings.ToolsDirectory, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
    if (!audioOnly) arguments.AddRange(["-f", "lavfi", "-i", "color=c=blue:s=320x180:r=25"]);
    arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1"]);
    if (!audioOnly) arguments.AddRange(["-c:v", "libx264", "-pix_fmt", "yuv420p"]);
    arguments.AddRange(["-c:a", "aac", Path.Combine(output, name)]);
    foreach (var arg in arguments) psi.ArgumentList.Add(arg);
    using var process = Process.Start(psi)!;
    string error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    Assert(process.ExitCode == 0, "generate " + name + " " + error);
}
await Generate("valid.mp4", false);
await DownloadService.VerifyMediaAsync(settings.ToolsDirectory, Path.Combine(output, "valid.mp4"), default);
Assert(true, "accept valid audio and video");
bool truncatedRejected = false;
try { await DownloadService.VerifyMediaAsync(settings.ToolsDirectory, Path.Combine(output, "valid.mp4"), default, 100); }
catch (IOException) { truncatedRejected = true; }
Assert(truncatedRejected, "reject truncated video by original duration");
await Generate("audio-only.m4a", true);
await File.WriteAllTextAsync(Path.Combine(output, "broken.mp4"), "not a media file");
foreach (string name in new[] { "audio-only.m4a", "broken.mp4" })
{
    bool rejected = false;
    try { await DownloadService.VerifyMediaAsync(settings.ToolsDirectory, Path.Combine(output, name), default); } catch (Exception) { rejected = true; }
    Assert(rejected, "reject incomplete/corrupt media " + name);
}
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    bool canceled = false;
    try { await new DownloadService().AnalyzeAsync("https://www.bilibili.com/video/BV1cABMYtEj2/", settings, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
    Assert(canceled, "canceled task never launches extraction");
}
if (args.Length == 2 && args[0] is "--live" or "--player" or "--session-browser")
{
    var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var uiThread = new Thread(() =>
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
        try
        {
    if (args[0] == "--session-browser")
    {
        var view = new Microsoft.Web.WebView2.Wpf.WebView2();
        var host = new System.Windows.Window { Content = view, Width = 100, Height = 100, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        host.Show();
        try
        {
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(output, "isolated-session-browser"));
            await view.EnsureCoreWebView2Async(environment);
            await view.CoreWebView2.Profile.ClearBrowsingDataAsync(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllProfile);
            var fakeCookie = view.CoreWebView2.CookieManager.CreateCookie("SESSDATA", fakeSecret, ".bilibili.com", "/");
            fakeCookie.IsHttpOnly = true; fakeCookie.IsSecure = true; fakeCookie.Expires = DateTime.UtcNow.AddDays(1);
            view.CoreWebView2.CookieManager.AddOrUpdateCookie(fakeCookie);
            var captured = await AccountSession.CaptureAsync(view.CoreWebView2);
            Assert(captured.Any(c => c.Name == "SESSDATA" && c.Value == fakeSecret && c.HttpOnly), "capture scoped HttpOnly cookie from isolated WebView2 profile");
            testSession.Save(new SavedSession(identity!, captured));
            view.CoreWebView2.CookieManager.DeleteAllCookies();
            Assert(!(await AccountSession.CaptureAsync(view.CoreWebView2)).Any(c => c.Name == "SESSDATA"), "browser cookie clearing works");
            await testSession.RestoreAsync(view.CoreWebView2);
            Assert((await AccountSession.CaptureAsync(view.CoreWebView2)).Any(c => c.Name == "SESSDATA" && c.Value == fakeSecret && c.HttpOnly && c.Secure), "restore encrypted credential into empty WebView2 profile");
            await view.CoreWebView2.Profile.ClearBrowsingDataAsync(Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.AllProfile);
            Assert((await AccountSession.CaptureAsync(view.CoreWebView2)).Count == 0, "logout clears isolated browser session");
            testSession.Forget();
        }
        finally { view.Dispose(); host.Close(); }
        finished.TrySetResult();
        return;
    }
    if (args[0] == "--player")
    {
        var control = new LocalPlayer();
        var host = new System.Windows.Window { Content = control, Width = 1000, Height = 650, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        host.Show();
        try
        {
            control.Volume = 0;
            await control.OpenAsync(Path.GetFullPath(args[1]));
            await Task.Delay(1200);
            Assert(control.IsPlaying && control.Position.TotalSeconds > 0.3 && control.VideoWidth == 852 && control.HasAudio, "embedded player plays the real cached video");
            var toggle = (System.Windows.Controls.Button)control.FindName("ToggleButton");
            toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Task.Delay(200);
            double pausedAt = control.Position.TotalSeconds;
            await Task.Delay(500);
            Assert(!control.IsPlaying && Math.Abs(control.Position.TotalSeconds - pausedAt) < 0.2, "pause button holds playback position");
            var slider = (System.Windows.Controls.Slider)control.FindName("Timeline");
            slider.Value = 700;
            toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Task.Delay(1300);
            Assert(control.Position.TotalSeconds > 700 && control.IsPlaying, "timeline seeks and play button resumes");
            control.Seek(TimeSpan.FromSeconds(60));
            await Task.Delay(700);
            control.Pause();
            control.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)control.ActualWidth, (int)control.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(control);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var screenshot = File.Create(Path.Combine(output, "player-preview.png"))) png.Save(screenshot);
            Assert(File.Exists(Path.Combine(output, "player-preview.png")), "render embedded player preview");
            bool missingRejected = false;
            try { await control.OpenAsync(Path.Combine(output, "does-not-exist.mp4")); } catch (FileNotFoundException) { missingRejected = true; }
            Assert(missingRejected, "missing local file is handled");
        }
        finally { control.Dispose(); host.Close(); }
        finished.TrySetResult();
        return;
    }
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    var service = new DownloadService();
    var info = await service.AnalyzeAsync(args[1], settings, deadline.Token);
    Console.WriteLine($"LIVE: {info.Title}; duration={info.Duration}; heights={string.Join(',', info.Heights)}");
    var job = new CacheJob { Url = info.Url, Title = info.Title, Height = 480, Folder = Path.Combine(output, "live") };
    string file = await service.DownloadAsync(job, settings, new Progress<DownloadUpdate>(p => Console.WriteLine(p.Detail)), deadline.Token);
    Assert(File.Exists(file), "real Bilibili download and media validation");
    Console.WriteLine(file);
    // Use Windows' local media playback stack, with a file URI only, to verify playback and seeking.
    var player = new System.Windows.Media.MediaPlayer { Volume = 0 };
    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.MediaOpened += (_, _) => opened.TrySetResult();
    player.MediaFailed += (_, e) => opened.TrySetException(e.ErrorException);
    try
    {
        player.Open(new Uri(file));
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(20));
        player.Play();
        await Task.Delay(2000);
        Assert(player.Position.TotalSeconds > 0.5 && player.NaturalVideoWidth > 0 && player.HasAudio, "Windows local playback advances with video and audio");
        player.Position = TimeSpan.FromSeconds(700);
        await Task.Delay(1500);
        Assert(player.Position.TotalSeconds > 700, "local playback can seek near the end");
    }
    finally { player.Close(); }
        finished.TrySetResult();
        }
        catch (Exception ex) { finished.TrySetException(ex); }
        finally { app.Shutdown(); }
        };
        app.Run();
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    await finished.Task;
}

sealed class HangingHandler : System.Net.Http.HttpMessageHandler
{
    protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable");
    }
}
