using System.Diagnostics;
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
    private static readonly TimeSpan SeekHoldDelay = TimeSpan.FromMilliseconds(350);
    private const double HoldSeekRate = 5;
    private static readonly DependencyPropertyKey IsPlayingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsPlaying), typeof(bool), typeof(LocalPlayer), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPlayingProperty = IsPlayingPropertyKey.DependencyProperty;

    private readonly MediaPlayer player = new();
    private readonly VideoDrawing drawing;
    private readonly DispatcherTimer timer, seekTimer;
    private TaskCompletionSource? pending;
    private string? currentVideoFile;
    private SubtitleTrack? subtitles;
    private CancellationTokenSource? subtitleLoad;
    private bool updating, dragging, ready, disposed;
    private Key seekKey;
    private long seekPressedAt;
    private long? seekStartedAt;
    private double seekStartPosition;
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); private set => SetValue(IsPlayingPropertyKey, value); }
    public TimeSpan Position => player.Position;
    public TimeSpan Duration => player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero;
    public int VideoWidth => player.NaturalVideoWidth;
    public bool HasAudio => player.HasAudio;
    public double Volume { get => player.Volume; set => VolumeSlider.Value = Math.Clamp(value, 0, 1); }
    private bool CanSeekFromKeyboard => IsVisible && ready && !dragging &&
        !VolumeSlider.IsKeyboardFocusWithin && !SpeedBox.IsDropDownOpen;

    public LocalPlayer()
    {
        InitializeComponent();
        drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, 16, 9) };
        VideoSurface.Source = new DrawingImage(drawing);
        player.Volume = VolumeSlider.Value;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => RefreshPosition();
        seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        seekTimer.Tick += (_, _) =>
        {
            if (!CanSeekFromKeyboard || seekKey == Key.None || !Keyboard.IsKeyDown(seekKey) ||
                (Keyboard.Modifiers & ~ModifierKeys.Shift) != ModifierKeys.None) EndKeyboardSeek();
            else UpdateKeyboardSeek(Stopwatch.GetTimestamp());
        };
        Unloaded += (_, _) => EndKeyboardSeek();
        player.MediaOpened += (_, _) =>
        {
            drawing.Rect = new Rect(0, 0, Math.Max(1, player.NaturalVideoWidth), Math.Max(1, player.NaturalVideoHeight));
            SetViewportReady(true);
            Timeline.Maximum = Math.Max(1, Duration.TotalSeconds); Timeline.IsEnabled = true; ToggleButton.IsEnabled = true;
            DurationLabel.Text = FormatTime(Duration);
            MediaInfo.Text = $"本地文件 · {player.NaturalVideoWidth} × {player.NaturalVideoHeight} · {FormatTime(Duration)} · 无需网络";
            RefreshPosition();
            if (IsVisible) Play();
            pending?.TrySetResult();
        };
        player.MediaEnded += (_, _) =>
        {
            bool resetPosition = IsPlaying && seekKey == Key.None;
            Pause();
            if (resetPosition) Seek(TimeSpan.Zero);
        };
        player.MediaFailed += (_, e) =>
        {
            EndKeyboardSeek();
            SetViewportReady(false);
            currentVideoFile = null;
            ResetSubtitles();
            IsPlaying = false; timer.Stop(); Timeline.IsEnabled = false; ToggleButton.IsEnabled = false;
            EmptyHint.Text = "无法播放此文件，请检查文件或编码格式";
            MediaInfo.Text = e.ErrorException.Message; pending?.TrySetException(e.ErrorException);
        };
    }
    public async Task OpenAsync(string file)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) throw new FileNotFoundException("本地视频文件不存在。", file);
        EndKeyboardSeek();
        pending?.TrySetCanceled();
        SetViewportReady(false);
        ResetSubtitles();
        string videoFile = Path.GetFullPath(file);
        currentVideoFile = videoFile;
        player.Close(); timer.Stop(); IsPlaying = false;
        Timeline.IsEnabled = false; ToggleButton.IsEnabled = false;
        NowPlaying.Text = Path.GetFileNameWithoutExtension(file);
        MediaInfo.Text = "正在打开本地视频…"; EmptyHint.Text = "正在加载…";
        updating = true; Timeline.Value = 0; updating = false;
        PositionLabel.Text = "00:00"; DurationLabel.Text = "00:00";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion;
        player.Open(new Uri(videoFile));
        _ = LoadSubtitlesAsync(videoFile);
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException)
        {
            if (pending == completion)
            {
                SetViewportReady(false);
                currentVideoFile = null;
                ResetSubtitles();
                player.Close();
                EmptyHint.Text = MediaInfo.Text = "打开视频超时，请重试或选择其他文件。";
            }
            throw;
        }
    }

    private void SetViewportReady(bool isReady)
    {
        ready = isReady;
        VideoSurface.Visibility = isReady ? Visibility.Visible : Visibility.Collapsed;
        VideoPlaceholder.Visibility = isReady ? Visibility.Collapsed : Visibility.Visible;
    }

    private void VideoViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ((FrameworkElement)sender).Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
    }

    public void Play()
    {
        if (!ready) return;
        if (Duration > TimeSpan.Zero && player.Position >= Duration) Seek(TimeSpan.Zero);
        player.Play(); IsPlaying = true; timer.Start();
    }
    public void Pause()
    {
        player.Pause(); IsPlaying = false; timer.Stop(); RefreshPosition();
    }
    public void Seek(TimeSpan position)
    {
        if (!ready) return;
        var target = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds));
        player.Position = target;
        RefreshPosition(target);
    }

    internal bool HandlePlayerKeyDown(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (!IsVisible || !ready || SpeedBox.IsDropDownOpen || (modifiers & ~ModifierKeys.Shift) != ModifierKeys.None)
            return false;

        if (e.Key is Key.Left or Key.Right) return HandleSeekKeyDown(e, modifiers);

        if (e.Key == Key.Space && modifiers == ModifierKeys.None && !SpeedBox.IsKeyboardFocusWithin)
        {
            if (!e.IsRepeat) TogglePlayback();
            return true;
        }

        int speedStep = e.Key switch
        {
            Key.Add or Key.OemPlus => 1,
            Key.Subtract => -1,
            Key.OemMinus when modifiers == ModifierKeys.None => -1,
            _ => 0
        };
        if (speedStep == 0 || SpeedBox.Items.Count == 0) return false;

        SpeedBox.SelectedIndex = Math.Clamp(SpeedBox.SelectedIndex + speedStep, 0, SpeedBox.Items.Count - 1);
        return true;
    }

    private bool HandleSeekKeyDown(KeyEventArgs e, ModifierKeys modifiers)
    {
        if (!CanSeekFromKeyboard || Duration <= TimeSpan.Zero) return false;
        if (e.IsRepeat) return true;

        seekKey = e.Key;
        seekPressedAt = Stopwatch.GetTimestamp();
        seekStartedAt = null;
        double step = modifiers == ModifierKeys.Shift ? 1 : 5;
        SeekFromKeyboard(Position.TotalSeconds + (seekKey == Key.Right ? step : -step));
        seekTimer.Start();
        return true;
    }

    internal bool HandleSeekKeyUp(KeyEventArgs e)
    {
        if (e.Key != seekKey || seekKey == Key.None) return false;
        if (CanSeekFromKeyboard && seekStartedAt.HasValue) UpdateKeyboardSeek(Stopwatch.GetTimestamp());
        EndKeyboardSeek();
        return true;
    }

    internal void EndKeyboardSeek()
    {
        seekTimer.Stop();
        seekKey = Key.None;
        seekStartedAt = null;
    }

    private void UpdateKeyboardSeek(long now)
    {
        if (seekStartedAt is not long startedAt)
        {
            if (Stopwatch.GetElapsedTime(seekPressedAt, now) < SeekHoldDelay) return;
            seekStartedAt = now;
            seekStartPosition = Position.TotalSeconds;
            return;
        }

        // Use one position anchor so ongoing playback and keyboard repeat do not change the hold rate.
        double elapsed = Stopwatch.GetElapsedTime(startedAt, now).TotalSeconds;
        double rate = seekKey == Key.Right ? HoldSeekRate : -HoldSeekRate;
        SeekFromKeyboard(seekStartPosition + elapsed * rate);
    }

    private void SeekFromKeyboard(double seconds)
    {
        var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration.TotalSeconds));
        if (target >= Duration && IsPlaying) Pause();
        if (target != Position) Seek(target);
    }

    private void RefreshPosition(TimeSpan? seekTarget = null)
    {
        if (!ready) return;
        TimeSpan position = seekTarget ?? player.Position;
        RefreshSubtitle(position);
        if (dragging) return;
        updating = true; Timeline.Value = position.TotalSeconds; updating = false;
        PositionLabel.Text = FormatTime(position);
    }

    private void RefreshSubtitle(TimeSpan position)
    {
        string text = ready ? subtitles?.TextAt(position) ?? "" : "";
        if (SubtitleText.Text == text) return;
        SubtitleText.Text = text;
        SubtitleOverlay.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetSubtitles()
    {
        subtitleLoad?.Cancel();
        subtitleLoad = null;
        subtitles = null;
        timer.Interval = TimeSpan.FromMilliseconds(200);
        SubtitleButton.ToolTip = "选择字幕文件";
        RefreshSubtitle(TimeSpan.Zero);
    }

    private async Task LoadSubtitlesAsync(string videoFile, string? chosenFile = null)
    {
        subtitleLoad?.Cancel();
        using var cancellation = new CancellationTokenSource();
        subtitleLoad = cancellation;
        try
        {
            var result = await Task.Run(() => ReadSubtitlesAsync(videoFile, chosenFile, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || disposed || currentVideoFile != videoFile || result is not { } loaded) return;
            subtitles = loaded.Track;
            SubtitleButton.ToolTip = $"已加载 {Path.GetFileName(loaded.File)}\n点击更换字幕";
            timer.Interval = TimeSpan.FromMilliseconds(50);
            RefreshSubtitle(ready ? Position : TimeSpan.Zero);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (IsSubtitleLoadError(ex))
        {
            if (chosenFile != null && !cancellation.IsCancellationRequested && !disposed && Window.GetWindow(this) is { } owner)
                MessageBox.Show(owner, "无法加载字幕。\n" + ex.Message, "BiliCache", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            if (subtitleLoad == cancellation) subtitleLoad = null;
        }
    }

    private static async Task<(SubtitleTrack Track, string File)?> ReadSubtitlesAsync(string videoFile, string? chosenFile, CancellationToken token)
    {
        if (chosenFile != null) return (await SubtitleTrack.LoadAsync(chosenFile, token), chosenFile);
        foreach (string candidate in SubtitleTrack.FindForVideo(videoFile))
        {
            token.ThrowIfCancellationRequested();
            try { return (await SubtitleTrack.LoadAsync(candidate, token), candidate); }
            catch (Exception ex) when (IsSubtitleLoadError(ex)) { /* Try the next matching sidecar. */ }
        }
        return null;
    }

    private static bool IsSubtitleLoadError(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException;

    private async void ChooseSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (!ready || currentVideoFile is not string videoFile || Window.GetWindow(this) is not { } owner) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择字幕文件",
            InitialDirectory = Path.GetDirectoryName(videoFile) ?? "",
            Filter = SubtitleTrack.FileDialogFilter
        };
        if (dialog.ShowDialog(owner) == true && currentVideoFile == videoFile)
            await LoadSubtitlesAsync(videoFile, dialog.FileName);
    }
    private static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    private void TogglePlayback()
    {
        EndKeyboardSeek();
        if (IsPlaying) Pause();
        else Play();
    }
    private void Toggle_Click(object sender, RoutedEventArgs e) => TogglePlayback();
    private void Video_Click(object sender, MouseButtonEventArgs e) => TogglePlayback();
    private void Back_Click(object sender, RoutedEventArgs e) { EndKeyboardSeek(); Seek(Position - TimeSpan.FromSeconds(10)); }
    private void Forward_Click(object sender, RoutedEventArgs e) { EndKeyboardSeek(); Seek(Position + TimeSpan.FromSeconds(10)); }
    private void Stop_Click(object sender, RoutedEventArgs e) { EndKeyboardSeek(); Pause(); Seek(TimeSpan.Zero); }
    private void Timeline_DragStarted(object sender, DragStartedEventArgs e) { EndKeyboardSeek(); dragging = true; }
    private void Timeline_DragCompleted(object sender, DragCompletedEventArgs e) { dragging = false; Seek(TimeSpan.FromSeconds(Timeline.Value)); }
    private void Timeline_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || updating) return;
        PositionLabel.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        if (!dragging) { EndKeyboardSeek(); Seek(TimeSpan.FromSeconds(e.NewValue)); }
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
        disposed = true; EndKeyboardSeek(); timer.Stop(); ResetSubtitles(); pending?.TrySetCanceled(); player.Close();
    }
}
