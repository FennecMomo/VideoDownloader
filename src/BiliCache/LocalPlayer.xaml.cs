using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BiliCache;

public partial class LocalPlayer : UserControl, IDisposable
{
    private readonly MediaPlayer player = new();
    private readonly VideoDrawing drawing;
    private readonly DispatcherTimer timer;
    private TaskCompletionSource? pending;
    private bool updating, dragging, ready, disposed;
    public bool IsPlaying { get; private set; }
    public TimeSpan Position => player.Position;
    public TimeSpan Duration => player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
    public int VideoWidth => player.NaturalVideoWidth;
    public bool HasAudio => player.HasAudio;
    public double Volume { get => player.Volume; set => VolumeSlider.Value = Math.Clamp(value, 0, 1); }

    public LocalPlayer()
    {
        InitializeComponent();
        drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, 16, 9) };
        VideoSurface.Source = new DrawingImage(drawing);
        player.Volume = VolumeSlider.Value;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => RefreshPosition();
        player.MediaOpened += (_, _) =>
        {
            ready = true;
            drawing.Rect = new Rect(0, 0, Math.Max(1, player.NaturalVideoWidth), Math.Max(1, player.NaturalVideoHeight));
            Timeline.Maximum = Math.Max(1, Duration.TotalSeconds); Timeline.IsEnabled = true; ToggleButton.IsEnabled = true;
            DurationLabel.Text = FormatTime(Duration);
            MediaInfo.Text = $"本地文件 · {player.NaturalVideoWidth} × {player.NaturalVideoHeight} · {FormatTime(Duration)} · 无需网络";
            EmptyHint.Visibility = Visibility.Collapsed;
            Play(); pending?.TrySetResult();
        };
        player.MediaEnded += (_, _) => { Pause(); Seek(TimeSpan.Zero); };
        player.MediaFailed += (_, e) =>
        {
            ready = false; IsPlaying = false; timer.Stop(); Timeline.IsEnabled = false; ToggleButton.IsEnabled = false;
            EmptyHint.Text = "无法播放此文件，请检查文件或编码格式"; EmptyHint.Visibility = Visibility.Visible;
            MediaInfo.Text = e.ErrorException.Message; pending?.TrySetException(e.ErrorException);
        };
    }
    public async Task OpenAsync(string file)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) throw new FileNotFoundException("本地视频文件不存在。", file);
        pending?.TrySetCanceled();
        player.Close(); timer.Stop(); ready = false; IsPlaying = false;
        Timeline.IsEnabled = false; ToggleButton.IsEnabled = false;
        NowPlaying.Text = Path.GetFileNameWithoutExtension(file);
        MediaInfo.Text = "正在打开本地视频…"; EmptyHint.Text = "正在加载…"; EmptyHint.Visibility = Visibility.Visible;
        updating = true; Timeline.Value = 0; updating = false;
        PositionLabel.Text = "00:00"; DurationLabel.Text = "00:00";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion;
        player.Open(new Uri(Path.GetFullPath(file)));
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException)
        {
            if (pending == completion) { player.Close(); ready = false; MediaInfo.Text = "打开视频超时，请重试或选择其他文件。"; }
            throw;
        }
    }
    public void Play()
    {
        if (!ready) return;
        player.Play(); IsPlaying = true; ToggleButton.Content = "暂停"; timer.Start();
    }
    public void Pause()
    {
        player.Pause(); IsPlaying = false; ToggleButton.Content = "播放"; timer.Stop(); RefreshPosition();
    }
    public void Seek(TimeSpan position)
    {
        if (!ready) return;
        player.Position = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds)); RefreshPosition();
    }
    private void RefreshPosition()
    {
        if (!ready || dragging) return;
        updating = true; Timeline.Value = player.Position.TotalSeconds; updating = false;
        PositionLabel.Text = FormatTime(player.Position);
    }
    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    private void Toggle_Click(object sender, RoutedEventArgs e) { if (IsPlaying) Pause(); else Play(); }
    private void Video_Click(object sender, MouseButtonEventArgs e) => Toggle_Click(sender, e);
    private void Back_Click(object sender, RoutedEventArgs e) => Seek(player.Position - TimeSpan.FromSeconds(10));
    private void Forward_Click(object sender, RoutedEventArgs e) => Seek(player.Position + TimeSpan.FromSeconds(10));
    private void Stop_Click(object sender, RoutedEventArgs e) { Pause(); Seek(TimeSpan.Zero); }
    private void Timeline_DragStarted(object sender, DragStartedEventArgs e) => dragging = true;
    private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e) { dragging = false; Seek(TimeSpan.FromSeconds(Timeline.Value)); }
    private void Timeline_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || updating) return;
        PositionLabel.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        if (!dragging) Seek(TimeSpan.FromSeconds(e.NewValue));
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => player.Volume = e.NewValue;
    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is ComboBoxItem item && double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double rate)) player.SpeedRatio = rate;
    }
    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "视频文件|*.mp4;*.mkv;*.mov;*.wmv;*.avi|所有文件|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await OpenAsync(dialog.FileName); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MediaInfo.Text = ex.Message; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer.Stop(); pending?.TrySetCanceled(); player.Close();
    }
}
