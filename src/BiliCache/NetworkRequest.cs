using System.Net.Http;

namespace BiliCache;

public static class NetworkRequest
{
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try { return await client.SendAsync(request, cancellationToken); }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("B站账号验证连接超时，尚未开始下载视频。请检查当前网络、DNS 或代理设置后重试；已保留登录状态。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException($"B站账号验证连接失败（{ex.HttpRequestError}），尚未开始下载视频。请检查网络或代理设置后重试；已保留登录状态。", ex);
        }
    }
}
