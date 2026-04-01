using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Playout.Core.Models;
using Playout.Engine.Abstractions;
using Playout.Engine.Scheduling;
using Playout.Engine.Types;

namespace Playout.Engine;

public sealed class PlayoutEngine
{
    readonly IFrameSource _source;
    readonly IVideoSink _sink;
    readonly PlaylistResolver _resolver;
    private CGElementsManager? _cgManager;
    private Playout.Core.Services.PlayoutLogger? _logger;
    private Playout.Core.Services.AdScheduler? _adScheduler;
    private IEnumerable<Media>? _fillers;
    private IEnumerable<PlaylistItem>? _manualPlaylist;
    private PlaylistItem? _currentItem;
    private float _leftLevel, _rightLevel;

    public PlayoutEngine(IFrameSource source, IVideoSink sink, IEnumerable<PlaylistItem> scheduledItems)
    {
        _source = source;
        _sink = sink;
        _resolver = new PlaylistResolver(scheduledItems);
    }

    public bool Loop { get; set; }
    public void SetCGManager(CGElementsManager manager) => _cgManager = manager;
    public void SetLogger(Playout.Core.Services.PlayoutLogger logger) => _logger = logger;
    public void SetAdScheduler(Playout.Core.Services.AdScheduler adScheduler) => _adScheduler = adScheduler;
    public void SetFillers(IEnumerable<Media> fillers) => _fillers = fillers;
    public void SetWeeklyPrograms(IEnumerable<WeeklyProgram> programs) => _resolver.SetWeeklyPrograms(programs);
    public void SetManualPlaylist(IEnumerable<PlaylistItem> manualPlaylist) => _manualPlaylist = manualPlaylist;

    public (float Left, float Right) GetAudioLevels() => (_leftLevel, _rightLevel);
    public PlaylistItem? GetCurrentItem() => _currentItem;
    public PlaylistItem? GetNextItem() => _resolver.NowOrNext(DateTimeOffset.UtcNow.AddSeconds(1), _fillers);

    public async Task RunAsync(CancellationToken ct)
    {
        PlaylistItem? lastItem = null;
        DateTime lastAdCheck = DateTime.MinValue;
        int manualIndex = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;

                // 1. Check for Manual Playlist (Highest Priority)
                if (_manualPlaylist != null && manualIndex < _manualPlaylist.Count())
                {
                    var item = _manualPlaylist.ElementAt(manualIndex);
                    _currentItem = item;
                    _logger?.LogEntry(item, "Manual Start");
                    await PlayItemAsync(item, ct);
                    _logger?.LogEntry(item, "Manual End");
                    manualIndex++;
                    continue;
                }

                // 2. Check for scheduled Ads
                if (_adScheduler != null && now.Minute % 15 == 0 && now.Minute != lastAdCheck.Minute)
                {
                    lastAdCheck = now.DateTime;
                    var ads = _adScheduler.GetAdsForSlot(now);
                    if (ads.Any())
                    {
                        foreach (var ad in ads)
                        {
                            _currentItem = ad;
                            _logger?.LogEntry(ad, "Ad Start");
                            await PlayItemAsync(ad, ct);
                            _logger?.LogEntry(ad, "Ad End");
                        }
                        _currentItem = null;
                        continue;
                    }
                }

                // 3. Check Schedule / Weekly / Filler
                var scheduledItem = _resolver.NowOrNext(now, _fillers);
                
                if (scheduledItem == null)
                {
                    _currentItem = null;
                    if (Loop && _resolver.HasItems)
                    {
                        _resolver.OffsetStartTimes(now);
                        continue;
                    }
                    // Black frame
                    _sink.Send(new VideoFrame(1280, 720, Types.PixelFormat.BGRA, new byte[1280 * 720 * 4], 0));
                    _leftLevel = _rightLevel = 0;
                    await Task.Delay(33, ct);
                    continue;
                }

                if (scheduledItem == lastItem)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                lastItem = scheduledItem;
                _currentItem = scheduledItem;
                _logger?.LogEntry(scheduledItem, "Started");
                await PlayItemAsync(scheduledItem, ct);
                _logger?.LogEntry(scheduledItem, "Completed");
            }
        }
        finally
        {
            _currentItem = null;
            if (_sink is IDisposable d) d.Dispose();
        }
    }

    async Task PlayItemAsync(PlaylistItem item, CancellationToken ct)
    {
        await foreach (var frame in _source.ReadFramesAsync(item, ct))
        {
            // Update audio meters
            UpdateLevels(frame);

            // Apply CG Overlays
            if (_cgManager != null)
            {
                _cgManager.ApplyOverlays(frame);
            }
            
            _sink.Send(frame);
            
            // Auto-advance check: if we've reached the end of this item's slot
            if (item.FixedStartUtc.HasValue && item.Duration > TimeSpan.Zero)
            {
                var endAt = item.FixedStartUtc.Value + item.Duration;
                if (DateTimeOffset.UtcNow >= endAt) break;
            }
        }
    }

    private void UpdateLevels(VideoFrame frame)
    {
        if (frame.AudioData == null || frame.AudioData.Length == 0)
        {
            _leftLevel = _rightLevel = 0;
            return;
        }

        float maxL = 0, maxR = 0;
        for (int i = 0; i < frame.AudioData.Length; i += 2)
        {
            maxL = Math.Max(maxL, Math.Abs(frame.AudioData[i]));
            if (i + 1 < frame.AudioData.Length)
                maxR = Math.Max(maxR, Math.Abs(frame.AudioData[i + 1]));
        }
        _leftLevel = maxL;
        _rightLevel = maxR;
    }
}

public sealed class CGElementsManager : IDisposable
{
    private readonly List<CGElement> _elements = new();
    private readonly Dictionary<Guid, VideoOverlaySource> _videoSources = new();
    private readonly object _lock = new();
    private float _tickerOffset = 0;

    public void UpdateElements(IEnumerable<CGElement> elements)
    {
        lock (_lock)
        {
            _elements.Clear();
            _elements.AddRange(elements);

            // Manage video and gif sources
            var mediaElements = elements.Where(e => e.Type == CGElementType.Video || e.Type == CGElementType.Gif).ToList();
            var toRemove = _videoSources.Keys.Where(id => mediaElements.All(e => e.Id != id)).ToList();
            foreach (var id in toRemove)
            {
                _videoSources[id].Dispose();
                _videoSources.Remove(id);
            }

            foreach (var el in mediaElements)
            {
                if (!_videoSources.ContainsKey(el.Id))
                {
                    _videoSources[el.Id] = new VideoOverlaySource(el.Content);
                }
            }
        }
    }

    public void ApplyOverlays(VideoFrame frame)
    {
        lock (_lock)
        {
            if (!_elements.Any(e => e.IsVisible)) return;

            // We use System.Drawing to render overlays onto the BGRA buffer
            using var bitmap = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, frame.Width, frame.Height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Marshal.Copy(frame.Data, 0, bitmapData.Scan0, frame.Data.Length);
            bitmap.UnlockBits(bitmapData);

            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;

                foreach (var element in _elements.Where(e => e.IsVisible))
                {
                    float x = (float)(element.X * frame.Width);
                    float y = (float)(element.Y * frame.Height);

                    switch (element.Type)
                    {
                        case CGElementType.Logo:
                            // Draw a professional logo placeholder
                            using (var brush = new SolidBrush(Color.FromArgb(180, 0, 122, 204)))
                            {
                                g.FillEllipse(brush, x, y, 60, 60);
                                g.DrawString("TV", new Font("Arial", 20, FontStyle.Bold), Brushes.White, x + 10, y + 15);
                            }
                            break;

                        case CGElementType.Ticker:
                            // Scrolling text at the bottom
                            float barHeight = frame.Height * 0.08f;
                            float barY = frame.Height - barHeight;
                            g.FillRectangle(new SolidBrush(Color.FromArgb(150, 0, 0, 0)), 0, barY, frame.Width, barHeight);
                            
                            _tickerOffset -= 5; // Move ticker
                            if (_tickerOffset < -2000) _tickerOffset = frame.Width;
                            
                            g.DrawString(element.Content, new Font("Segoe UI", 24), Brushes.White, _tickerOffset, barY + 5);
                            break;

                        case CGElementType.LowerThird:
                            // Name strap
                            float ltW = 400, ltH = 80;
                            g.FillRectangle(new SolidBrush(Color.FromArgb(200, 0, 122, 204)), x, y, ltW, ltH);
                            g.DrawString(element.Content, new Font("Segoe UI", 20, FontStyle.Bold), Brushes.White, x + 20, y + 10);
                            g.DrawString("LIVE BROADCAST", new Font("Segoe UI", 12), Brushes.LightGray, x + 20, y + 45);
                            break;

                        case CGElementType.Image:
                        case CGElementType.Gif:
                            // Static image or GIF (handled by VideoOverlaySource)
                            if (element.Type == CGElementType.Gif)
                            {
                                if (_videoSources.TryGetValue(element.Id, out var gifSource))
                                {
                                    var gf = gifSource.GetNextFrame();
                                    if (gf != null)
                                    {
                                        using var oBmp = new Bitmap(gf.Width, gf.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                        var oRect = new Rectangle(0, 0, gf.Width, gf.Height);
                                        var oData = oBmp.LockBits(oRect, ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                        Marshal.Copy(gf.Data, 0, oData.Scan0, gf.Data.Length);
                                        oBmp.UnlockBits(oData);
                                        g.DrawImage(oBmp, x, y, (float)(element.Width * frame.Width), (float)(element.Height * frame.Height));
                                    }
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(element.Content) && System.IO.File.Exists(element.Content))
                            {
                                try
                                {
                                    using (var img = Image.FromFile(element.Content))
                                    {
                                        float w = (float)(element.Width > 0 ? element.Width * frame.Width : img.Width);
                                        float h = (float)(element.Height > 0 ? element.Height * frame.Height : img.Height);
                                        g.DrawImage(img, x, y, w, h);
                                    }
                                }
                                catch { }
                            }
                            break;

                        case CGElementType.Video:
                            // Video overlay (looping)
                            if (_videoSources.TryGetValue(element.Id, out var source))
                            {
                                var overlayFrame = source.GetNextFrame();
                                if (overlayFrame != null)
                                {
                                    using var overlayBmp = new Bitmap(overlayFrame.Width, overlayFrame.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                    var oRect = new Rectangle(0, 0, overlayFrame.Width, overlayFrame.Height);
                                    var oData = overlayBmp.LockBits(oRect, ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                    Marshal.Copy(overlayFrame.Data, 0, oData.Scan0, overlayFrame.Data.Length);
                                    overlayBmp.UnlockBits(oData);

                                    float w = (float)(element.Width > 0 ? element.Width * frame.Width : overlayFrame.Width);
                                    float h = (float)(element.Height > 0 ? element.Height * frame.Height : overlayFrame.Height);
                                    g.DrawImage(overlayBmp, x, y, w, h);
                                }
                            }
                            break;

                        case CGElementType.Html:
                            // Placeholder for HTML (rendering HTML is complex without a library)
                            using (var brush = new SolidBrush(Color.FromArgb(100, 0, 0, 0)))
                            {
                                g.FillRectangle(brush, x, y, (float)(element.Width * frame.Width), (float)(element.Height * frame.Height));
                                g.DrawString($"HTML: {element.Content}", new Font("Arial", 10), Brushes.Lime, x + 5, y + 5);
                            }
                            break;
                    }
                }
            }

            // Copy back to frame data
            var finalData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Marshal.Copy(finalData.Scan0, frame.Data, 0, frame.Data.Length);
            bitmap.UnlockBits(finalData);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var s in _videoSources.Values) s.Dispose();
            _videoSources.Clear();
        }
    }
}

internal sealed class VideoOverlaySource : IDisposable
{
    private readonly string _path;
    private System.Diagnostics.Process? _process;
    private readonly byte[] _buffer;
    private readonly int _width = 400; // Default small overlay size
    private readonly int _height = 400;
    private readonly object _lock = new();

    public VideoOverlaySource(string path)
    {
        _path = path;
        _buffer = new byte[_width * _height * 4];
        StartProcess();
    }

    private void StartProcess()
    {
        if (!File.Exists(_path)) return;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -stream_loop -1 -i \"{_path}\" -vf scale={_width}:{_height} -pix_fmt bgra -f rawvideo -",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _process = System.Diagnostics.Process.Start(psi);
    }

    public VideoFrame? GetNextFrame()
    {
        if (_process == null || _process.HasExited) return null;

        lock (_lock)
        {
            try
            {
                int read = 0;
                while (read < _buffer.Length)
                {
                    int n = _process.StandardOutput.BaseStream.Read(_buffer, read, _buffer.Length - read);
                    if (n <= 0) return null;
                    read += n;
                }
                return new VideoFrame(_width, _height, Playout.Engine.Types.PixelFormat.BGRA, (byte[])_buffer.Clone(), 0);
            }
            catch { return null; }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_process != null && !_process.HasExited) _process.Kill();
            _process?.Dispose();
        }
        catch { }
    }
}
