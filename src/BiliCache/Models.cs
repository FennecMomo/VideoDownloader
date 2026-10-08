using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BiliCache;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class CacheJob : Observable
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Height { get; set; }
    public string Folder { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    private string state = "等待中";
    public string State { get => state; set => Set(ref state, value); }
    private string detail = "等待缓存";
    public string Detail { get => detail; set => Set(ref detail, value); }
    private double progress;
    public double Progress { get => progress; set => Set(ref progress, value); }
    [JsonIgnore] public string Quality => Height == 0 ? "最高可用" : $"最高 {Height}p";
}

public sealed class Settings
{
    public string DownloadDirectory { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "BiliCache");
    public string CookieFile { get; set; } = "";
    public string ToolsDirectory { get; set; } = "";
    public bool UseAccountSession { get; set; }
    public string AccountName { get; set; } = "";
}

public sealed record VideoInfo(string Url, string Title, string Author, double Duration, List<int> Heights);
public sealed record QualityOption(int Height, string Label);
public sealed record DownloadUpdate(double Percent, string Detail);
