using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playout.Core.Models;
using Playout.Engine;
using Playout.Engine.Abstractions;
using Playout.Engine.Sources;
using Playout.Engine.Types;

namespace Playout.Wpf;

public sealed class MediaNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public string Icon => IsDirectory ? "📁" : "🎬";
    public ObservableCollection<MediaNode> Children { get; } = new();

    public void LoadChildren()
    {
        if (!IsDirectory) return;
        try
        {
            Children.Clear();
            var exts = new[] { ".mp4", ".mov", ".mkv", ".avi", ".mxf" };
            
            // Add Directories
            foreach (var d in Directory.EnumerateDirectories(FullPath))
            {
                Children.Add(new MediaNode { Name = System.IO.Path.GetFileName(d), FullPath = d, IsDirectory = true });
            }
            
            // Add Files
            foreach (var f in Directory.EnumerateFiles(FullPath))
            {
                var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                if (exts.Contains(ext))
                {
                    Children.Add(new MediaNode { Name = System.IO.Path.GetFileName(f), FullPath = f, IsDirectory = false });
                }
            }
        }
        catch { }
    }
}

public sealed class PlaylistItemView : System.ComponentModel.INotifyPropertyChanged
{
    private TimeSpan _markIn = TimeSpan.Zero;
    private TimeSpan _markOut = TimeSpan.Zero;

    public string FullPath { get; set; } = "";
    public string FileName => System.IO.Path.GetFileName(FullPath);
    public string Format => System.IO.Path.GetExtension(FullPath).ToUpperInvariant().TrimStart('.');
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);
    
    public TimeSpan MarkIn 
    { 
        get => _markIn; 
        set { _markIn = value; OnPropertyChanged(nameof(MarkIn)); OnPropertyChanged(nameof(DurationStr)); }
    }
    
    public TimeSpan MarkOut 
    { 
        get => _markOut == TimeSpan.Zero ? Duration : _markOut; 
        set { _markOut = value; OnPropertyChanged(nameof(MarkOut)); OnPropertyChanged(nameof(DurationStr)); }
    }

    public string DurationStr => (MarkOut - MarkIn).ToString(@"hh\:mm\:ss");

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    readonly ObservableCollection<PlaylistItemView> _playlist = new();
    readonly ObservableCollection<CGElement> _cgElements = new();
    private readonly Playout.Core.Services.DatabaseService _db;
    private readonly Playout.Core.Services.AdScheduler _adScheduler = new();
    private readonly Playout.Core.Services.PlayoutLogger _logger = new();
    private readonly CGElementsManager _cgManager = new();
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;
    private readonly System.Windows.Threading.DispatcherTimer _meterTimer;
    WriteableBitmap? _wb;
    CancellationTokenSource? _cts;
    PlayoutEngine? _engine;
    IVideoSink? _sink;
    IFrameSource? _source;

    readonly ObservableCollection<Schedule> _daySchedules = new();
    readonly ObservableCollection<PlaylistItem> _scheduleItems = new();
    readonly ObservableCollection<WeeklyProgram> _weeklyPrograms = new();
    readonly ObservableCollection<Media> _fillerPool = new();

    readonly ObservableCollection<MediaNode> _mediaTree = new();
    bool _isOnAir;
    private PlaybackMode _activeMode = PlaybackMode.None;
    private readonly System.Threading.SemaphoreSlim _playSwitchLock = new(1, 1);

    enum PlaybackMode
    {
        None,
        Playlist,
        Schedule
    }

    public MainWindow()
    {
        InitializeComponent();
        _db = new Playout.Core.Services.DatabaseService();
        MediaTreeView.ItemsSource = _mediaTree;
        PlaylistList.ItemsSource = _playlist;
        CGElementsList.ItemsSource = _cgElements;
        DaySchedulesList.ItemsSource = _daySchedules;
        ScheduleItemsList.ItemsSource = _scheduleItems;
        WeeklyProgramsList.ItemsSource = _weeklyPrograms;
        FillerPoolList.ItemsSource = _fillerPool;
        Loaded += OnLoaded;

        _clockTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (s, e) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clockTimer.Start();

        _meterTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _meterTimer.Tick += (s, e) =>
        {
            if (_engine != null)
            {
                var levels = _engine.GetAudioLevels();
                AudioMeterL.Value = levels.Left * 100;
                AudioMeterR.Value = levels.Right * 100;

                // Update Next Item and Countdown
                var current = _engine.GetCurrentItem();
                var next = _engine.GetNextItem();

                if (current != null)
                {
                    var remaining = (current.FixedStartUtc + current.Duration) - DateTimeOffset.UtcNow;
                    if (remaining.HasValue && remaining.Value > TimeSpan.Zero)
                    {
                        CountdownText.Text = remaining.Value.ToString(@"hh\:mm\:ss");
                    }
                    else
                    {
                        CountdownText.Text = "00:00:00";
                    }
                }
                else
                {
                    CountdownText.Text = "00:00:00";
                }

                if (next != null)
                {
                    NextItemText.Text = $"NEXT: {next.FileName}";
                }
                else
                {
                    NextItemText.Text = "NEXT: ---";
                }
            }
            else
            {
                AudioMeterL.Value = 0;
                AudioMeterR.Value = 0;
                CountdownText.Text = "00:00:00";
                NextItemText.Text = "NEXT: ---";
            }
        };
        _meterTimer.Start();
        UpdateControlStates();
    }
    async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        ExplorerDrivesCombo.ItemsSource = drives;
        
        var first = drives.FirstOrDefault();
        if (first != null)
        {
            ExplorerDrivesCombo.SelectedItem = first;
        }

        if (NdiAvailable())
        {
            NdiCheck.IsEnabled = true;
            NdiCheck.IsChecked = true;
            SetStatus("NDI Runtime detected.");
        }
        else
        {
            SetStatus("NDI Runtime not found. Please install NDI 5 Runtime.");
        }

        // Initialize CG with a default Logo
        _cgManager.UpdateElements(new[] {
            new CGElement { Name = "Main Logo", Type = CGElementType.Logo, IsVisible = true, X = 0.9, Y = 0.1 }
        });
        foreach (var el in new[] { new CGElement { Name = "Main Logo", Type = CGElementType.Logo, IsVisible = true, X = 0.9, Y = 0.1 } })
            _cgElements.Add(el);

        // Load media from DB
        var savedMedia = await _db.GetAllMediaAsync();
        foreach (var m in savedMedia.Where(m => m.Category == "Filler")) _fillerPool.Add(m);
        
        SetStatus($"System ready. Database contains {savedMedia.Count} indexed files.");
    }

    void UpdateControlStates()
    {
        if (OnAirBtn == null) return;
        OnAirBtn.Background = _isOnAir ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51));
        OnAirText.Text = _isOnAir ? "ON AIR" : "OFF AIR";
        OnAirText.Foreground = _isOnAir ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
        OnAirIndicator.Background = _isOnAir ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red) : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51));

        PlayPlaylistBtn.IsEnabled = _isOnAir && _playlist.Count > 0;
        PlayScheduleBtn.IsEnabled = _isOnAir && DaySchedulesList.SelectedItem is Schedule && _scheduleItems.Count > 0;

        PlayPlaylistBtn.Background = _activeMode == PlaybackMode.Playlist
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 136, 45))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66));
        PlayScheduleBtn.Background = _activeMode == PlaybackMode.Schedule
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 136, 45))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 62, 66));
    }

    void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateControlStates();
    }

    void OnAirBtn_Click(object sender, RoutedEventArgs e)
    {
        _isOnAir = !_isOnAir;
        if (!_isOnAir)
        {
            _cts?.Cancel();
            _activeMode = PlaybackMode.None;
        }
        UpdateControlStates();
        SetStatus(_isOnAir ? "ON AIR enabled. Choose Play Playlist or Play Schedule." : "OFF AIR.");
    }
    
    void SetStatus(string msg)
    {
        Dispatcher.Invoke(() => StatusText.Text = msg);
    }
    bool FfmpegAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog();
        var result = dlg.ShowDialog();
        if (result == System.Windows.Forms.DialogResult.OK && Directory.Exists(dlg.SelectedPath))
        {
            FolderBox.Text = dlg.SelectedPath;
            LoadExplorerRoot(dlg.SelectedPath);
        }
    }

    void LoadExplorerRoot(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        folder = folder.Trim();
        if (!Directory.Exists(folder))
        {
            SetStatus($"Cannot access: {folder}");
            return;
        }
        _mediaTree.Clear();
        string name = folder;
        if (!folder.StartsWith(@"\\"))
        {
            var n = folder.TrimEnd('\\');
            n = System.IO.Path.GetFileName(n);
            if (!string.IsNullOrWhiteSpace(n)) name = n;
        }
        var root = new MediaNode { Name = name, FullPath = folder, IsDirectory = true };
        root.LoadChildren();
        _mediaTree.Add(root);
    }

    void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderBox.Text) || !Directory.Exists(FolderBox.Text)) return;
        LoadExplorerRoot(FolderBox.Text);
        SetStatus($"Media Explorer root: {FolderBox.Text}");
    }

    void ExplorerDrivesCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ExplorerDrivesCombo.SelectedItem is string root)
        {
            FolderBox.Text = root;
            LoadExplorerRoot(root);
            SetStatus($"Media Explorer root: {root}");
        }
    }

    void FolderBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            LoadExplorerRoot(FolderBox.Text);
            SetStatus($"Media Explorer root: {FolderBox.Text}");
            e.Handled = true;
        }
    }

    void MediaTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is MediaNode node && node.IsDirectory)
        {
            node.LoadChildren();
        }
    }

    void MediaTreeView_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed && MediaTreeView.SelectedItem is MediaNode node && !node.IsDirectory)
        {
            var data = new System.Windows.DataObject();
            data.SetData(System.Windows.DataFormats.Text, node.FullPath);
            data.SetData(System.Windows.DataFormats.FileDrop, new[] { node.FullPath });
            System.Windows.DragDrop.DoDragDrop(MediaTreeView, data, System.Windows.DragDropEffects.Copy);
        }
    }

    bool TryGetSelectedExplorerFilePath(out string path)
    {
        path = "";
        if (MediaTreeView.SelectedItem is not MediaNode node) return false;
        if (node.IsDirectory) return false;
        path = node.FullPath;
        return File.Exists(path);
    }

    async Task<MediaMetadata> GetVideoMetadata(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return new MediaMetadata { Duration = TimeSpan.FromSeconds(10) };
            var output = await p.StandardOutput.ReadToEndAsync();
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length >= 4)
            {
                int.TryParse(lines[0].Trim(), out int w);
                int.TryParse(lines[1].Trim(), out int h);
                string fpsRaw = lines[2].Trim();
                double.TryParse(lines[3].Trim(), out double durSec);

                return new MediaMetadata
                {
                    Width = w,
                    Height = h,
                    FpsStr = fpsRaw,
                    Duration = TimeSpan.FromSeconds(durSec)
                };
            }
        }
        catch { }
        return new MediaMetadata { Duration = TimeSpan.FromSeconds(10) };
    }

    struct MediaMetadata
    {
        public int Width;
        public int Height;
        public string FpsStr;
        public TimeSpan Duration;
    }

    void PlaylistList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.Text) || e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) e.Effects = System.Windows.DragDropEffects.Copy;
        else e.Effects = System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    async void PlaylistList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var exts = new[] { ".mp4", ".mov", ".mkv", ".avi", ".mxf" };
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files)
                {
                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (exts.Contains(ext))
                    {
                        var meta = await GetVideoMetadata(f);
                        var view = new PlaylistItemView { FullPath = f, Duration = meta.Duration };
                        _playlist.Add(view);
                        await _db.SaveMediaAsync(new Media {
                            Id = Guid.NewGuid(),
                            Path = f,
                            FileName = view.FileName,
                            Duration = meta.Duration,
                            Width = meta.Width,
                            Height = meta.Height,
                            Category = "Program"
                        });
                    }
                }
            }
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.Text))
        {
            var path = e.Data.GetData(System.Windows.DataFormats.Text) as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (exts.Contains(ext))
                {
                    var meta = await GetVideoMetadata(path);
                    var view = new PlaylistItemView { FullPath = path, Duration = meta.Duration };
                    _playlist.Add(view);
                    await _db.SaveMediaAsync(new Media {
                        Id = Guid.NewGuid(),
                        Path = path,
                        FileName = view.FileName,
                        Duration = meta.Duration,
                        Width = meta.Width,
                        Height = meta.Height,
                        Category = "Program"
                    });
                }
            }
        }
    }

    async void ScheduleItemsList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (DaySchedulesList.SelectedItem is not Schedule s)
        {
            SetStatus("Please select a schedule first.");
            return;
        }

        var exts = new[] { ".mp4", ".mov", ".mkv", ".avi", ".mxf" };
        var paths = new List<string>();

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files) paths.AddRange(files);
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.Text))
        {
            if (e.Data.GetData(System.Windows.DataFormats.Text) is string path) paths.Add(path);
        }

        foreach (var p in paths)
        {
            var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
            if (exts.Contains(ext))
            {
                var meta = await GetVideoMetadata(p);
                var lastItem = _scheduleItems.LastOrDefault();
                var startTime = lastItem != null ? lastItem.FixedStartUtc!.Value + lastItem.Duration : s.StartTimeUtc;

                var item = new PlaylistItem
                {
                    Id = Guid.NewGuid(),
                    MediaPath = p,
                    FileName = System.IO.Path.GetFileName(p),
                    FixedStartUtc = startTime,
                    Duration = meta.Duration,
                    MarkIn = TimeSpan.Zero,
                    MarkOut = meta.Duration,
                    SortOrder = _scheduleItems.Count
                };
                _scheduleItems.Add(item);
                
                await _db.SaveMediaAsync(new Media {
                    Id = Guid.NewGuid(),
                    Path = p,
                    FileName = item.FileName,
                    Duration = meta.Duration,
                    Width = meta.Width,
                    Height = meta.Height,
                    Category = "Program"
                });
            }
        }
    }

    async void PlayPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isOnAir)
        {
            SetStatus("Enable ON AIR first.");
            return;
        }
        if (_playlist.Count == 0)
        {
            SetStatus("Playlist is empty.");
            return;
        }

        var manualItems = new List<PlaylistItem>();
        foreach (var p in _playlist)
        {
            manualItems.Add(new PlaylistItem
            {
                Id = Guid.NewGuid(),
                MediaPath = p.FullPath,
                FileName = p.FileName,
                Duration = p.MarkOut - p.MarkIn,
                MarkIn = p.MarkIn,
                MarkOut = p.MarkOut
            });
        }

        await SwitchAndStartAsync(
            mode: PlaybackMode.Playlist,
            scheduledItems: new List<PlaylistItem>(),
            manualItems: manualItems,
            enableAutomation: false);
    }

    async void PlayScheduleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isOnAir)
        {
            SetStatus("Enable ON AIR first.");
            return;
        }
        if (DaySchedulesList.SelectedItem is not Schedule)
        {
            SetStatus("Select a schedule first.");
            return;
        }
        if (_scheduleItems.Count == 0)
        {
            SetStatus("Selected schedule has no items.");
            return;
        }

        var scheduledItems = _scheduleItems
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.FixedStartUtc ?? DateTimeOffset.MinValue)
            .ToList();

        var current = DateTimeOffset.UtcNow;
        foreach (var it in scheduledItems)
        {
            it.FixedStartUtc = current;
            current += it.Duration + it.Padding;
        }

        await SwitchAndStartAsync(
            mode: PlaybackMode.Schedule,
            scheduledItems: scheduledItems,
            manualItems: new List<PlaylistItem>(),
            enableAutomation: true);
    }

    async Task SwitchAndStartAsync(PlaybackMode mode, List<PlaylistItem> scheduledItems, List<PlaylistItem> manualItems, bool enableAutomation)
    {
        await _playSwitchLock.WaitAsync();
        try
        {
            if (_cts != null)
            {
                _cts.Cancel();
                // wait for current engine to stop and cleanup
                for (int i = 0; i < 100; i++)
                {
                    if (_cts == null) break;
                    await Task.Delay(50);
                }
            }

            if (!_isOnAir)
            {
                _activeMode = PlaybackMode.None;
                UpdateControlStates();
                return;
            }

            _activeMode = mode;
            UpdateControlStates();
            await StartPlayoutInternalAsync(scheduledItems: scheduledItems, manualItems: manualItems, enableAutomation: enableAutomation);
        }
        finally
        {
            _playSwitchLock.Release();
        }
    }

    async Task StartPlayoutInternalAsync(List<PlaylistItem> scheduledItems, List<PlaylistItem> manualItems, bool enableAutomation)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        UpdateControlStates();
        EnsurePreview(1280, 720);
        var sinks = new System.Collections.Generic.List<IVideoSink> { new UiPreviewSink(_wb!, Dispatcher) };
        
        bool ndiRequested = false;
        Dispatcher.Invoke(() => ndiRequested = NdiCheck.IsChecked == true);

        if (ndiRequested)
        {
            if (NdiAvailable())
            {
                string name = "";
                Dispatcher.Invoke(() => name = string.IsNullOrWhiteSpace(NdiNameBox.Text) ? "Playout Channel 1" : NdiNameBox.Text);
                SetStatus($"NDI enabled: {name}");
                try
                {
                    var ndiSink = new Playout.Engine.Sinks.NdiSink(name);
                    sinks.Add(ndiSink);
                }
                catch (Exception ex)
                {
                    SetStatus($"NDI Error: {ex.Message}");
                }
            }
            else
            {
                SetStatus("NDI runtime not found; preview only.");
            }
        }

        var fps = new Rational(30000, 1001);

        if (StreamCheck.IsChecked == true)
        {
            if (FfmpegAvailable())
            {
                var url = StreamUrlBox.Text;
                SetStatus($"Streaming enabled: {url}");
                try
                {
                    var streamSink = new Playout.Engine.Sinks.FfmpegStreamSink(url, 1280, 720, fps);
                    sinks.Add(streamSink);
                }
                catch (Exception ex)
                {
                    SetStatus($"Streaming Error: {ex.Message}");
                }
            }
            else
            {
                SetStatus("FFmpeg not found; streaming disabled.");
            }
        }

        _sink = new CompositeSink(sinks);
        if (UseFfmpegCheck.IsChecked == true)
        {
            if (!FfmpegAvailable())
            {
                SetStatus("ffmpeg not found on PATH; using synthetic preview.");
                _source = new SyntheticFrameSource(1280, 720, fps);
            }
            else
            {
                SetStatus("Using ffmpeg for decode.");
                _source = new FfmpegProcessFrameSource(1280, 720, fps);
            }
        }
        else
        {
            SetStatus("Using synthetic frames.");
            _source = new SyntheticFrameSource(1280, 720, fps);
        }

        _engine = new PlayoutEngine(_source, _sink, scheduledItems)
        {
            Loop = LoopCheck.IsChecked == true
        };
        _engine.SetCGManager(_cgManager);
        _engine.SetLogger(_logger);
        _engine.SetAdScheduler(_adScheduler);
        if (manualItems.Count > 0) _engine.SetManualPlaylist(manualItems);

        if (enableAutomation)
        {
            var allMedia = await _db.GetAllMediaAsync();
            _adScheduler.UpdateAdPool(allMedia);
            _engine.SetFillers(_fillerPool);
            _engine.SetWeeklyPrograms(_weeklyPrograms);
        }

        try
        {
            await _engine.RunAsync(_cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus($"Playout Error: {ex.Message}");
        }
        finally
        {
            StopPlayout();
        }
    }

    void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void StopPlayout()
    {
        _cts = null;
        _activeMode = PlaybackMode.None;
        SetStatus("Playout stopped.");
        UpdateControlStates();
    }

    void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = PlaylistList.SelectedIndex;
        if (selectedIndex > 0)
        {
            _playlist.Move(selectedIndex, selectedIndex - 1);
        }
    }

    void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var selectedIndex = PlaylistList.SelectedIndex;
        if (selectedIndex >= 0 && selectedIndex < _playlist.Count - 1)
        {
            _playlist.Move(selectedIndex, selectedIndex + 1);
        }
    }

    void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("Clear all items from playlist?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _playlist.Clear();
        }
    }

    void AddCGLogo_Click(object sender, RoutedEventArgs e)
    {
        var el = new CGElement { Name = "New Logo", Type = CGElementType.Logo, X = 0.9, Y = 0.1, IsVisible = true };
        _cgElements.Add(el);
        _cgManager.UpdateElements(_cgElements);
    }

    void AddCGTicker_Click(object sender, RoutedEventArgs e)
    {
        var el = new CGElement { Name = "New Ticker", Type = CGElementType.Ticker, Content = "BREAKING NEWS: Professional Playout System is Live! ", IsVisible = true };
        _cgElements.Add(el);
        _cgManager.UpdateElements(_cgElements);
    }

    void AddCGLowerThird_Click(object sender, RoutedEventArgs e)
    {
        var el = new CGElement { Name = "Lower Third", Type = CGElementType.LowerThird, Content = "John Doe", X = 0.1, Y = 0.8, IsVisible = true };
        _cgElements.Add(el);
        _cgManager.UpdateElements(_cgElements);
    }

    void AddCGImage_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
        if (ofd.ShowDialog() == true)
        {
            var el = new CGElement 
            { 
                Name = $"Image: {System.IO.Path.GetFileName(ofd.FileName)}", 
                Type = CGElementType.Image, 
                Content = ofd.FileName, 
                X = 0.1, 
                Y = 0.1, 
                Width = 0.2, 
                Height = 0.2, 
                IsVisible = true 
            };
            _cgElements.Add(el);
            _cgManager.UpdateElements(_cgElements);
            RefreshCGDesigner();
        }
    }

    void AddCGGif_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "GIF Files (*.gif)|*.gif" };
        if (ofd.ShowDialog() == true)
        {
            var el = new CGElement 
            { 
                Name = $"GIF: {System.IO.Path.GetFileName(ofd.FileName)}", 
                Type = CGElementType.Gif, 
                Content = ofd.FileName, 
                X = 0.1, 
                Y = 0.1, 
                Width = 0.2, 
                Height = 0.2, 
                IsVisible = true 
            };
            _cgElements.Add(el);
            _cgManager.UpdateElements(_cgElements);
            RefreshCGDesigner();
        }
    }

    void AddCGHtml_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "HTML Files (*.html;*.htm)|*.html;*.htm" };
        string content = "";
        if (ofd.ShowDialog() == true)
        {
            content = ofd.FileName;
        }
        else
        {
            // Prompt for URL if file browse cancelled
            // Simplified: just a placeholder
            content = "http://localhost:8080";
        }

        var el = new CGElement 
        { 
            Name = "HTML Overlay", 
            Type = CGElementType.Html, 
            Content = content, 
            X = 0, 
            Y = 0, 
            Width = 1, 
            Height = 1, 
            IsVisible = true 
        };
        _cgElements.Add(el);
        _cgManager.UpdateElements(_cgElements);
        RefreshCGDesigner();
    }

    void AddCGVideo_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Video Files (*.mp4;*.mov;*.mkv;*.avi)|*.mp4;*.mov;*.mkv;*.avi" };
        if (ofd.ShowDialog() == true)
        {
            var el = new CGElement 
            { 
                Name = $"Video: {System.IO.Path.GetFileName(ofd.FileName)}", 
                Type = CGElementType.Video, 
                Content = ofd.FileName, 
                X = 0.1, 
                Y = 0.1, 
                Width = 0.3, 
                Height = 0.3, 
                IsVisible = true 
            };
            _cgElements.Add(el);
            _cgManager.UpdateElements(_cgElements);
            RefreshCGDesigner();
        }
    }

    // FILLER & WEEKLY HANDLERS
    async void FillerPoolList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var exts = new[] { ".mp4", ".mov", ".mkv", ".avi", ".mxf" };
        var paths = new List<string>();

        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files) paths.AddRange(files);
        }
        else if (e.Data.GetDataPresent(System.Windows.DataFormats.Text))
        {
            if (e.Data.GetData(System.Windows.DataFormats.Text) is string path) paths.Add(path);
        }

        foreach (var p in paths)
        {
            var ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
            if (exts.Contains(ext))
            {
                var meta = await GetVideoMetadata(p);
                var media = new Media 
                {
                    Id = Guid.NewGuid(),
                    Path = p,
                    FileName = System.IO.Path.GetFileName(p),
                    Duration = meta.Duration,
                    Category = "Filler"
                };
                await _db.SaveMediaAsync(media);
                _fillerPool.Add(media);
            }
        }
    }

    async void RemoveFiller_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is Media m)
        {
            m.Category = "Program";
            await _db.SaveMediaAsync(m);
            _fillerPool.Remove(m);
        }
    }

    async void AddWeeklyProgram_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedExplorerFilePath(out var path))
        {
            SetStatus("Please select a video from the Media Explorer first.");
            return;
        }

        if (TimeSpan.TryParse(WeeklyTimeBox.Text, out var startTime))
        {
            var meta = await GetVideoMetadata(path);
            DayOfWeek day = DayOfWeek.Monday;
            if (WeeklyDayCombo.SelectedItem is System.Windows.Controls.ComboBoxItem cbi && cbi.Content is string s)
            {
                if (!Enum.TryParse<DayOfWeek>(s, out day)) day = DayOfWeek.Monday;
            }
            var weekly = new WeeklyProgram
            {
                DayOfWeek = day,
                StartTime = startTime,
                MediaPath = path,
                Duration = meta.Duration
            };
            _weeklyPrograms.Add(weekly);
            SetStatus($"Added weekly program: {day} at {startTime}");
        }
        else
        {
            SetStatus("Invalid time format (HH:mm).");
        }
    }

    void RemoveWeekly_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is WeeklyProgram w)
        {
            _weeklyPrograms.Remove(w);
        }
    }

    private CGElement? _selectedCGElement;
    private bool _isDraggingCG;
    private System.Windows.Point _cgDragOffset;

    void CGElementsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedCGElement = CGElementsList.SelectedItem as CGElement;
        if (_selectedCGElement != null)
        {
            CGPropertiesPanel.Visibility = Visibility.Visible;
            CGPropName.Text = _selectedCGElement.Name;
            CGPropContent.Text = _selectedCGElement.Content;
            CGPropX.Text = _selectedCGElement.X.ToString("F3");
            CGPropY.Text = _selectedCGElement.Y.ToString("F3");
        }
        else
        {
            CGPropertiesPanel.Visibility = Visibility.Collapsed;
        }
        RefreshCGDesigner();
    }

    private void RefreshCGDesigner()
    {
        // Draw a placeholder frame with overlays
        var width = 1280;
        var height = 720;
        var frame = new VideoFrame(width, height, Playout.Engine.Types.PixelFormat.BGRA, new byte[width * height * 4], 0);
        
        // Draw some "placeholder" content (e.g., color bars or grey)
        for (int i = 0; i < frame.Data.Length; i += 4)
        {
            frame.Data[i] = 40; // B
            frame.Data[i + 1] = 40; // G
            frame.Data[i + 2] = 40; // R
            frame.Data[i + 3] = 255; // A
        }

        _cgManager.ApplyOverlays(frame);

        // Highlight selected element
        if (_selectedCGElement != null)
        {
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new System.Drawing.Rectangle(0, 0, width, height);
            var bitmapData = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(frame.Data, 0, bitmapData.Scan0, frame.Data.Length);
            bitmap.UnlockBits(bitmapData);

            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                float x = (float)(_selectedCGElement.X * width);
                float y = (float)(_selectedCGElement.Y * height);
                float w = 100, h = 100; // Default bounds for highlight

                if (_selectedCGElement.Type == CGElementType.Ticker) { x = 0; y = height * 0.92f; w = width; h = height * 0.08f; }
                else if (_selectedCGElement.Type == CGElementType.LowerThird) { w = 400; h = 80; }
                else { w = 60; h = 60; }

                using var pen = new System.Drawing.Pen(System.Drawing.Color.Yellow, 3);
                g.DrawRectangle(pen, x, y, w, h);
            }

            var finalData = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(finalData.Scan0, frame.Data, 0, frame.Data.Length);
            bitmap.UnlockBits(finalData);
        }

        var wb = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, width, height), frame.Data, width * 4, 0);
        CGDesignerPreview.Source = wb;
    }

    void CGDesignerPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectedCGElement == null) return;
        
        var pos = e.GetPosition(CGDesignerPreview);
        double normX = pos.X / CGDesignerPreview.ActualWidth;
        double normY = pos.Y / CGDesignerPreview.ActualHeight;

        // Check if we clicked near the element (simplified)
        if (Math.Abs(normX - _selectedCGElement.X) < 0.1 && Math.Abs(normY - _selectedCGElement.Y) < 0.1)
        {
            _isDraggingCG = true;
            _cgDragOffset = new System.Windows.Point(normX - _selectedCGElement.X, normY - _selectedCGElement.Y);
            CGDesignerPreview.CaptureMouse();
        }
    }

    void CGDesignerPreview_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isDraggingCG && _selectedCGElement != null)
        {
            var pos = e.GetPosition(CGDesignerPreview);
            double normX = pos.X / CGDesignerPreview.ActualWidth;
            double normY = pos.Y / CGDesignerPreview.ActualHeight;

            _selectedCGElement.X = Math.Clamp(normX - _cgDragOffset.X, 0, 1);
            _selectedCGElement.Y = Math.Clamp(normY - _cgDragOffset.Y, 0, 1);
            
            CGPropX.Text = _selectedCGElement.X.ToString("F3");
            CGPropY.Text = _selectedCGElement.Y.ToString("F3");

            _cgManager.UpdateElements(_cgElements);
            RefreshCGDesigner();
        }
    }

    void CGDesignerPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingCG = false;
        CGDesignerPreview.ReleaseMouseCapture();
    }

    void ApplyCGChanges_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCGElement != null)
        {
            _selectedCGElement.Name = CGPropName.Text;
            _selectedCGElement.Content = CGPropContent.Text;
            if (double.TryParse(CGPropX.Text, out double x)) _selectedCGElement.X = Math.Clamp(x, 0, 1);
            if (double.TryParse(CGPropY.Text, out double y)) _selectedCGElement.Y = Math.Clamp(y, 0, 1);
            
            _cgManager.UpdateElements(_cgElements);
            RefreshCGDesigner();
            SetStatus($"CG Element '{_selectedCGElement.Name}' updated.");
        }
    }

    void RemoveCGElement_Click(object sender, RoutedEventArgs e)
    {
        if (CGElementsList.SelectedItem is CGElement el)
        {
            _cgElements.Remove(el);
            _cgManager.UpdateElements(_cgElements);
            _selectedCGElement = null;
            RefreshCGDesigner();
        }
    }

    async void ScheduleCalendar_SelectedDatesChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ScheduleCalendar.SelectedDate.HasValue)
        {
            var date = ScheduleCalendar.SelectedDate.Value;
            ScheduleTitle.Text = $"Schedules for {date:yyyy-MM-dd}";
            var schedules = await _db.GetSchedulesForDateAsync(date);
            _daySchedules.Clear();
            foreach (var s in schedules) _daySchedules.Add(s);
            _scheduleItems.Clear();
            UpdateControlStates();
        }
    }

    async void DaySchedulesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DaySchedulesList.SelectedItem is Schedule s)
        {
            ScheduleTitle.Text = $"Items for {s.Name}";
            var items = await _db.GetPlaylistItemsAsync(s.Id);
            _scheduleItems.Clear();
            foreach (var item in items) _scheduleItems.Add(item);
        }
        UpdateControlStates();
    }

    async void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.DataContext is Schedule s)
        {
            if (System.Windows.MessageBox.Show($"Delete schedule '{s.Name}'?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                await _db.DeleteScheduleAsync(s.Id);
                _daySchedules.Remove(s);
                if (DaySchedulesList.SelectedItem == s)
                {
                    _scheduleItems.Clear();
                    ScheduleTitle.Text = "Select a date or schedule";
                }
                SetStatus($"Schedule '{s.Name}' deleted.");
            }
        }
    }

    async void CreateNewSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (!ScheduleCalendar.SelectedDate.HasValue) 
        {
            SetStatus("Please select a date on the calendar first.");
            return;
        }
        
        var date = ScheduleCalendar.SelectedDate.Value;
        var name = $"Schedule {date:yyyyMMdd} {DateTime.Now:HHmm}";
        
        // Use the currently loaded playlist as the starting point for the new schedule
        var items = new List<PlaylistItem>();
        var currentStart = new DateTimeOffset(date).AddHours(DateTime.Now.Hour); // Default start time
        
        for (int i = 0; i < _playlist.Count; i++)
        {
            var p = _playlist[i];
            items.Add(new PlaylistItem
            {
                Id = Guid.NewGuid(),
                MediaPath = p.FullPath,
                FileName = p.FileName,
                FixedStartUtc = currentStart,
                Duration = p.MarkOut - p.MarkIn,
                MarkIn = p.MarkIn,
                MarkOut = p.MarkOut,
                SortOrder = i
            });
            currentStart += (p.MarkOut - p.MarkIn);
        }

        var newSchedule = new Schedule
        {
            Name = name,
            ScheduledDate = date,
            StartTimeUtc = items.FirstOrDefault()?.FixedStartUtc ?? new DateTimeOffset(date),
            Status = ScheduleStatus.Pending,
            IsLoop = false,
            Items = items
        };

        await _db.SaveScheduleAsync(newSchedule);
        _daySchedules.Add(newSchedule);
        DaySchedulesList.SelectedItem = newSchedule;
        SetStatus($"Schedule '{newSchedule.Name}' created with {_playlist.Count} items.");
    }

    async void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (DaySchedulesList.SelectedItem is Schedule s)
        {
            // Simple conflict detection
            var items = _scheduleItems.OrderBy(i => i.FixedStartUtc).ToList();
            for (int i = 0; i < items.Count - 1; i++)
            {
                var current = items[i];
                var next = items[i + 1];
                if (current.FixedStartUtc + current.Duration > next.FixedStartUtc)
                {
                    if (System.Windows.MessageBox.Show(
                        $"Conflict detected between '{current.FileName}' and '{next.FileName}'. They overlap. Save anyway?", 
                        "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                    {
                        return;
                    }
                }
            }

            s.Items.Clear();
            s.Items.AddRange(_scheduleItems);
            await _db.SaveScheduleAsync(s);
            SetStatus("Schedule saved.");
        }
    }

    void LoadScheduleToPlayout_Click(object sender, RoutedEventArgs e)
    {
        if (DaySchedulesList.SelectedItem is Schedule s)
        {
            _playlist.Clear();
            foreach (var item in _scheduleItems)
            {
                _playlist.Add(new PlaylistItemView 
                { 
                    FullPath = item.MediaPath, 
                    Duration = item.Duration,
                    MarkIn = item.MarkIn,
                    MarkOut = item.MarkOut
                });
            }
            SetStatus($"Schedule '{s.Name}' loaded into playout.");
        }
    }

    void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new Microsoft.Win32.SaveFileDialog { Filter = "CSV Files (*.csv)|*.csv", FileName = "playout_log.csv" };
        if (sfd.ShowDialog() == true)
        {
            File.WriteAllText(sfd.FileName, _logger.ExportToCsv());
            SetStatus("Log exported.");
        }
    }

    void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        AsRunLogList.ItemsSource = _logger.GetHistory();
    }

    void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("Clear all logs?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            _logger.ClearLogs();
            AsRunLogList.ItemsSource = null;
            SetStatus("Logs cleared.");
        }
    }

    void EnsurePreview(int width, int height)
    {
        if (_wb == null || _wb.PixelWidth != width || _wb.PixelHeight != height)
        {
            _wb = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            PreviewImage.Source = _wb;
        }
    }

    void SavePlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_playlist.Any()) return;
        var sfd = new Microsoft.Win32.SaveFileDialog { Filter = "Playlist Files (*.txt)|*.txt" };
        if (sfd.ShowDialog() == true)
        {
            File.WriteAllLines(sfd.FileName, _playlist.Select(p => p.FullPath));
            SetStatus("Playlist saved.");
        }
    }

    async void LoadPlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog { Filter = "Playlist Files (*.txt)|*.txt" };
        if (ofd.ShowDialog() == true)
        {
            _playlist.Clear();
            foreach (var line in File.ReadAllLines(ofd.FileName))
            {
                if (File.Exists(line))
                {
                    var meta = await GetVideoMetadata(line);
                    var view = new PlaylistItemView { FullPath = line, Duration = meta.Duration };
                    _playlist.Add(view);
                    await _db.SaveMediaAsync(new Media {
                        Id = Guid.NewGuid(),
                        Path = line,
                        FileName = view.FileName,
                        Duration = meta.Duration,
                        Width = meta.Width,
                        Height = meta.Height,
                        Category = "Program"
                    });
                }
            }
            SetStatus("Playlist loaded.");
        }
    }

    void DeletePlaylistBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = PlaylistList.SelectedItems.Cast<PlaylistItemView>().ToList();
        foreach (var item in selected) _playlist.Remove(item);
    }
}

file sealed class UiPreviewSink : IVideoSink
{
    readonly WriteableBitmap _wb;
    readonly System.Windows.Threading.Dispatcher _dispatcher;
    public UiPreviewSink(WriteableBitmap wb, System.Windows.Threading.Dispatcher dispatcher)
    {
        _wb = wb;
        _dispatcher = dispatcher;
    }
    public void Send(VideoFrame frame)
    {
        _dispatcher.Invoke(() =>
        {
            if (_wb.PixelWidth != frame.Width || _wb.PixelHeight != frame.Height) return;
            var rect = new Int32Rect(0, 0, _wb.PixelWidth, _wb.PixelHeight);
            _wb.WritePixels(rect, frame.Data, frame.Width * 4, 0);
        });
    }
}
file sealed class CompositeSink : IVideoSink
{
    readonly System.Collections.Generic.List<IVideoSink> _sinks;
    public CompositeSink(System.Collections.Generic.IEnumerable<IVideoSink> sinks)
    {
        _sinks = new System.Collections.Generic.List<IVideoSink>(sinks);
    }
    public void Send(VideoFrame frame)
    {
        foreach (var s in _sinks) s.Send(frame);
    }
}
partial class MainWindow
{
    bool NdiAvailable()
    {
        try
        {
            // Try standard load
            var ok = System.Runtime.InteropServices.NativeLibrary.TryLoad("Processing.NDI.Lib.x64", out var handle);
            if (ok && handle != nint.Zero)
            {
                System.Runtime.InteropServices.NativeLibrary.Free(handle);
                return true;
            }

            // Try standard install paths
            var paths = new[]
            {
                @"C:\Program Files\NDI\NDI 5 Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                @"C:\Program Files\NDI\NDI 6 Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                @"C:\Program Files\NDI\NDI 5 Tools\Runtime\bin\x64\Processing.NDI.Lib.x64.dll",
                @"C:\Program Files\NDI\NDI 6 Tools\Runtime\bin\x64\Processing.NDI.Lib.x64.dll"
            };

            foreach (var p in paths)
            {
                if (File.Exists(p))
                {
                    ok = System.Runtime.InteropServices.NativeLibrary.TryLoad(p, out handle);
                    if (ok && handle != nint.Zero)
                    {
                        System.Runtime.InteropServices.NativeLibrary.Free(handle);
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }
}
