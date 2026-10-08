# BiliCache · 哔哩缓存

面向 Windows 的 Bilibili 本地视频缓存工具。WPF / .NET 10，通过 WebView2 正常加载视频页取得资源，再使用独立的 yt-dlp 和 FFmpeg 组件缓存和合并。当前版本 0.4.1。

0.4.1 修正账号验证超时被误报为取消的问题；取消时检查是否确有媒体数据；登录窗口显示页面网络错误并提供重新加载按钮。网络切换后如页面仍空白，可关闭并重开工具后重试；工具未强制配置 VPN，系统代理、DNS 与虚拟网卡均可能影响连接。

## 使用

1. 打开 `dist/BiliCache/BiliCache.exe`。
2. 粘贴 Bilibili 普通 BV / av 链接或 b23.tv 短链接，点击“解析视频”。
3. 选择清晰度，点击“加入缓存队列”。
4. 完成后选择任务并点击“播放选中视频”，自动切换到工具内的“本地播放器”分页。支持播放 / 暂停、进度拖动、前后 10 秒、停止、音量、0.5～2 倍速，也可以打开已有的本地视频。切换到其他分页时暂停，关闭工具时释放视频文件。

## 1080p 与登录

软件没有 480p 上限。清晰度列表仅显示页面实际返回的可下载媒体轨道；普通 1080p 通常需要登录，1080p+、高帧率等还可能需要相应会员权限。

1. 打开“设置”中的“登录 B站 / 重新登录”。
2. 自己在嵌入的 B站官方页面完成扫码或其他登录方式。
3. 工具自动检测 Cookie，并向 B站账号接口确认有效登录、账号 ID 和昵称。确认成功后加密保存会话，自动关闭窗口并应用账号；无需勾选或手动保存。
4. 输入框已有视频链接时自动重新解析并刷新清晰度。只有获得真实 1080p 轨道时才显示 1080p，旧的 480p 缓存不会自动变成高清；选择新清晰度建立新任务即可。

登录会话保存在应用独立的 WebView2 用户目录，并将 B站域名下的凭据通过 Windows DPAPI（CurrentUser）加密保存到 `%LOCALAPPDATA%/BiliCache/account.session`，用于恢复。程序不读取日常浏览器、不接收或记录登录密码，不将凭据写入普通设置 JSON 或日志。下次启动自动恢复账号；每次新解析时向 B站验证会话，过期则明确要求重新登录，临时网络失败不视为登出。

“退出工具内账号”清除该应用独立登录配置中的浏览数据及加密凭据，恢复匿名解析，保留本地缓存视频。账号操作需要等待进行中的解析 / 缓存结束。旧版导入 Cookie 文件仍为高级选项；自动登录成功后会清空该选项，确保使用刚登录的账号。

当前已验证示例视频的未登录 480p 缓存与工具内播放器；登录后 1080p 的真实下载需要用户完成登录后再验证。

默认保存到用户“视频”目录中的 BiliCache 文件夹。每个任务单独建目录，避免标题相同导致覆盖。任务记录和设置保存在 `%LOCALAPPDATA%/BiliCache`。移除任务记录不会删除磁盘文件；可以从文件夹自行管理。

第一版只缓存链接对应的视频 / 分P。指定分P需使用 `?p=2` 等参数，不会自动抓取整合集、收藏夹、番剧或直播。最高可用清晰度受当前登录权限限制。界面显示每条媒体流的进度，视频轨道完成后切换到音频轨道时，百分比可能重新开始；音视频合并和文件检查结束才标记完成。

未登录时使用独立的匿名浏览器会话解析；完成登录后自动使用账号会话。首次解析可能需要数十秒。直接通过命令行抓取页面可能遇到 HTTP 412；0.2.0 已将默认解析替换为浏览器加载，不再依赖该失败路径。

需要登录权限时，推荐使用上面的登录窗口；也可导入自己的 Netscape 格式 Cookie 文本文件。只向应用独立的 Cookie 导入会话写入 Bilibili 域名的 Cookie，不读取日常浏览器凭据。Cookie 文件可能被下载引擎更新，请保存在自己的受限目录，不要提交到源码库。需要人工验证、会员或其他权限的视频不保证可缓存，也不把试看片段当作完整缓存。

关闭程序会终止本程序启动的缓存子进程，保留片段；重新打开后选中任务点击“重试 / 继续”。续传能力取决于视频源和引擎，部分处理阶段可能需要重新下载。每次重试重新解析地址，不持久化易过期的 CDN 地址。

## 开发和发布

需要 Windows、.NET 10 SDK，以及 Microsoft Edge WebView2 Runtime。NuGet 使用微软官方的 Microsoft.Web.WebView2。当前电脑已检测到 WebView2 Runtime；其他电脑若缺少，可从 https://developer.microsoft.com/microsoft-edge/webview2/ 安装。

```powershell
./scripts/Install-Tools.ps1
dotnet build ./src/BiliCache -c Release
dotnet run --project ./src/BiliCache
./scripts/Publish.ps1 -SelfContained
```

安装脚本从 yt-dlp 及 yt-dlp/FFmpeg-Builds 官方仓库下载发布资产，并检查 GitHub 发布资产 SHA256 摘要。工具目录可以在设置里指定。发布时 `-SelfContained` 随包附带 .NET 运行时；省略时目标电脑需要 .NET 10 Desktop Runtime。

```powershell
dotnet run --project ./tests/BiliCache.Checks
# 真实视频测试，会在 test-output 下写入视频，失败时以非零状态退出：
dotnet run --project ./tests/BiliCache.Checks -- --live https://www.bilibili.com/video/BV1cABMYtEj2/
# 使用已有真实缓存验证播放器控件（替换为本地绝对路径）：
dotnet run --project ./tests/BiliCache.Checks -- --player 'C:\path\video.mp4'
# 使用伪造测试凭据验证独立浏览器配置中的加密保存、恢复和清除，不发送到 B站：
dotnet run --project ./tests/BiliCache.Checks -- --session-browser check
```

代码：`BrowserResolver.cs` 负责独立浏览器会话和页面资源解析；`DownloadService.cs` 负责参数化子进程、下载与输出验证；`Models.cs` 负责可绑定任务状态；`Storage.cs` 负责 JSON 原子替换保存；`CacheWindow.xaml(.cs)` 负责界面和串行队列。当前是轻量分层实现，窗口协调逻辑尚未抽为完整 MVVM ViewModel。

完成条件：下载引擎成功退出、最终文件在任务目录且非空、FFprobe 检查含有效音频与视频且时长与原视频相差不超过 3 秒。此检查不能替代对所有视频的实际播放器兼容性验证。临时下载地址文件在缓存结束或失败后移除，下次重试重新解析。
