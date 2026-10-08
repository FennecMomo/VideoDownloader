# Third-party components

- yt-dlp: https://github.com/yt-dlp/yt-dlp — source project under the Unlicense; standalone binary contains additional dependencies with their own licenses. See https://github.com/yt-dlp/yt-dlp/blob/master/THIRD_PARTY_LICENSES.txt and the exact release assets.
- FFmpeg / FFprobe: https://ffmpeg.org/ — downloaded from https://github.com/yt-dlp/FFmpeg-Builds. This application currently uses the win64 GPL build. Its license is included in tools/LICENSE.txt. See https://github.com/yt-dlp/FFmpeg-Builds for build scripts and source provenance and https://ffmpeg.org/legal.html for redistribution information.
- .NET: https://github.com/dotnet/runtime — MIT license and third-party notices included in self-contained runtime distributions.
- Microsoft.Web.WebView2 SDK: https://www.nuget.org/packages/Microsoft.Web.WebView2/ — Microsoft WebView2 SDK license; uses the separately installed Microsoft Edge WebView2 Runtime. See https://learn.microsoft.com/microsoft-edge/webview2/ for runtime distribution requirements.

Components run as separate executables. This local development package is not a completed public redistribution compliance package; before public distribution, preserve the exact component versions and fulfill their license and corresponding-source obligations.
