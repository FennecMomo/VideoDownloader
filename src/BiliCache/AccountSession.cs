using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace BiliCache;

public sealed record AccountIdentity(long Id, string Name);
public sealed record SessionCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly, double Expires);
public sealed record SavedSession(AccountIdentity Account, List<SessionCookie> Cookies);
public sealed class LoginExpiredException() : Exception("B站登录已失效，请点击“登录 B站”重新登录后再缓存高清视频。");

public sealed class AccountSession
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BiliCache.AccountSession.v1");
    private static readonly Uri VerifyUri = new("https://api.bilibili.com/x/web-interface/nav");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(12) };
    public string FilePath { get; }
    public AccountSession(string? directory = null) => FilePath = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliCache"), "account.session");

    public void Save(SavedSession session)
    {
        if (session.Account.Id <= 0 || !session.Cookies.Any(c => c.Name == "SESSDATA" && c.Value.Length > 0 && IsAllowed(c))) throw new InvalidOperationException("没有可保存的有效 B站登录会话。");
        var scoped = session with { Cookies = session.Cookies.Where(IsAllowed).ToList() };
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(scoped);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllBytes(FilePath + ".tmp", encrypted);
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SavedSession? Load()
    {
        if (!File.Exists(FilePath)) return null;
        byte[] plain;
        try { plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new InvalidOperationException("保存的登录会话无法解密，请重新登录 B站。"); }
        try { return JsonSerializer.Deserialize<SavedSession>(plain); }
        catch (JsonException) { throw new InvalidOperationException("保存的登录会话损坏，请重新登录 B站。"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Forget() { if (File.Exists(FilePath)) File.Delete(FilePath); }
    public static bool IsAllowed(SessionCookie cookie)
    {
        string domain = cookie.Domain.TrimStart('.');
        return (domain == "bilibili.com" || domain.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)) &&
            cookie.Name.Length > 0 && !cookie.Name.Any(c => char.IsControl(c) || c is ';' or '=' or ' ') &&
            !cookie.Value.Any(c => char.IsControl(c) || c == ';') &&
            (cookie.Expires <= 0 || cookie.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
    public static async Task<List<SessionCookie>> CaptureAsync(CoreWebView2 core)
    {
        var cookies = await core.CookieManager.GetCookiesAsync(VerifyUri.AbsoluteUri);
        return cookies.Select(c => new SessionCookie(c.Name, c.Value, c.Domain, c.Path, c.IsSecure, c.IsHttpOnly,
            c.IsSession ? 0 : new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds())).Where(IsAllowed).ToList();
    }
    public async Task RestoreAsync(CoreWebView2 core)
    {
        // Prefer cookies refreshed by Bilibili in the live app profile over an older saved copy.
        if ((await CaptureAsync(core)).Any(c => c.Name == "SESSDATA")) return;
        var saved = Load();
        if (saved == null) return;
        foreach (var item in saved.Cookies.Where(IsAllowed))
        {
            var cookie = core.CookieManager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
            cookie.IsSecure = item.Secure; cookie.IsHttpOnly = item.HttpOnly;
            if (item.Expires > 0) cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)item.Expires).UtcDateTime;
            core.CookieManager.AddOrUpdateCookie(cookie);
        }
    }
    public static async Task<AccountIdentity?> VerifyAsync(IEnumerable<SessionCookie> cookies, string userAgent, CancellationToken cancellationToken)
    {
        var relevant = cookies.Where(IsAllowed).Where(c =>
            (c.Domain.StartsWith('.') ? VerifyUri.Host == c.Domain[1..] || VerifyUri.Host.EndsWith(c.Domain, StringComparison.OrdinalIgnoreCase) : VerifyUri.Host.Equals(c.Domain, StringComparison.OrdinalIgnoreCase)) &&
            VerifyUri.AbsolutePath.StartsWith(c.Path, StringComparison.Ordinal)).ToList();
        if (!relevant.Any(c => c.Name == "SESSDATA" && c.Value.Length > 0)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, VerifyUri);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", relevant.Select(c => c.Name + "=" + c.Value)));
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.Referrer = new Uri("https://www.bilibili.com/");
        using var response = await NetworkRequest.SendAsync(Client, request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"暂时无法验证 B站账号（HTTP {(int)response.StatusCode}），已保留登录状态，请稍后重试。");
        return ParseIdentity(await response.Content.ReadAsStringAsync(cancellationToken));
    }
    public static AccountIdentity? ParseIdentity(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        int code = root.GetProperty("code").GetInt32();
        if (code == -101) return null;
        if (code != 0) throw new InvalidOperationException($"B站账号验证暂不可用（代码 {code}），请稍后重试。");
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("isLogin", out var login) || login.ValueKind != JsonValueKind.True) return null;
        if (!data.TryGetProperty("mid", out var mid) || !mid.TryGetInt64(out long id) || id <= 0 || !data.TryGetProperty("uname", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
            throw new InvalidOperationException("B站未返回完整的账号确认信息，请重试。");
        return new AccountIdentity(id, name.GetString()!);
    }
}
