using System.Diagnostics;
using System.Text;
using Playout.Core.Models;
using Playout.Engine.Abstractions;
using Playout.Engine.Types;

namespace Playout.Engine.Sources;

public sealed class FfmpegProcessFrameSource : IFrameSource
{
    readonly int _width;
    readonly int _height;
    readonly Rational _fps;
    public FfmpegProcessFrameSource(int width, int height, Rational fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
    }
    public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(PlaylistItem item, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.MediaPath)) yield break;

        // Playout standard: 48kHz Stereo
        const int sampleRate = 48000;
        const int channels = 2;
        var frameDur = TimeSpan.FromSeconds((double)_fps.Den / _fps.Num);
        var samplesPerFrame = (int)(sampleRate * frameDur.TotalSeconds);

        // We use TWO processes to get separate streams for video and audio.
        // This is much more robust than trying to separate a mixed stream from one process.
        
        // Support for Mark In/Out trimming
        string seekArg = item.MarkIn > TimeSpan.Zero ? $"-ss {item.MarkIn.TotalSeconds:F3}" : "";
        string durationArg = (item.MarkOut > TimeSpan.Zero && item.MarkOut > item.MarkIn) 
            ? $"-t {(item.MarkOut - item.MarkIn).TotalSeconds:F3}" 
            : "";

        var networkArgs = IsNetworkUrl(item.MediaPath)
            ? "-fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 -rw_timeout 5000000"
            : "";

        var vPsi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -nostdin {networkArgs} {seekArg} -i \"{item.MediaPath}\" {durationArg} -vf scale={_width}:{_height},fps={_fps.Num}/{_fps.Den} -pix_fmt bgra -f rawvideo -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ProcessStartInfo? aPsi = null;
        if (!IsNetworkUrl(item.MediaPath))
        {
            aPsi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-hide_banner -loglevel error -nostdin {networkArgs} {seekArg} -i \"{item.MediaPath}\" {durationArg} -af aresample={sampleRate},pan=stereo|c0=c0|c1=c1 -f f32le -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        using var vProc = Process.Start(vPsi);
        using var aProc = aPsi != null ? Process.Start(aPsi) : null;

        if (vProc == null) yield break;

        var vErr = new StringBuilder();
        var aErr = new StringBuilder();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && !vProc.StandardError.EndOfStream)
                {
                    var line = await vProc.StandardError.ReadLineAsync(ct);
                    if (line == null) break;
                    if (vErr.Length < 4096) vErr.AppendLine(line);
                }
            }
            catch { }
        }, ct);
        if (aProc != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && !aProc.StandardError.EndOfStream)
                    {
                        var line = await aProc.StandardError.ReadLineAsync(ct);
                        if (line == null) break;
                        if (aErr.Length < 4096) aErr.AppendLine(line);
                    }
                }
                catch { }
            }, ct);
        }

        var videoBytes = _width * _height * 4;
        var audioBytes = samplesPerFrame * channels * 4; // 4 bytes per float (f32le)

        var videoBuffer = new byte[videoBytes];
        var audioBuffer = new byte[audioBytes];
        
        var sw = Stopwatch.StartNew();
        long index = 0;
        bool shouldExit = false;
        
        var endAt = item.FixedStartUtc.HasValue 
            ? item.FixedStartUtc.Value + item.Duration 
            : DateTimeOffset.Now + item.Duration;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Read video
                int vRead = 0;
                while (vRead < videoBytes)
                {
                    int n = await vProc.StandardOutput.BaseStream.ReadAsync(videoBuffer.AsMemory(vRead, videoBytes - vRead), ct);
                    if (n <= 0) { shouldExit = true; break; }
                    vRead += n;
                }
                if (shouldExit) break;

                float[]? audioData = null;
                if (aProc != null)
                {
                    int aRead = 0;
                    while (aRead < audioBytes)
                    {
                        int n = await aProc.StandardOutput.BaseStream.ReadAsync(audioBuffer.AsMemory(aRead, audioBytes - aRead), ct);
                        if (n <= 0) break;
                        aRead += n;
                    }
                    if (aRead > 0)
                    {
                        audioData = new float[aRead / 4];
                        Buffer.BlockCopy(audioBuffer, 0, audioData, 0, aRead);
                    }
                }

                yield return new VideoFrame(_width, _height, PixelFormat.BGRA, (byte[])videoBuffer.Clone(), index++)
                {
                    AudioData = audioData,
                    AudioChannels = channels,
                    AudioSampleRate = sampleRate
                };

                // HIGH PRECISION TIMING:
                // We use a combination of Task.Delay for coarse waiting and SpinWait for fine-grained precision.
                var target = TimeSpan.FromTicks(frameDur.Ticks * index);
                var remaining = target - sw.Elapsed;
                
                if (remaining.TotalMilliseconds > 1)
                {
                    await Task.Delay(remaining - TimeSpan.FromMilliseconds(1), ct);
                }
                
                // Spin for the last ~1ms to get sub-millisecond precision
                while (sw.Elapsed < target)
                {
                    if (ct.IsCancellationRequested) { shouldExit = true; break; }
                }
                if (shouldExit) break;

                if (DateTimeOffset.Now >= endAt) break;
            }
        }
        finally
        {
            try { if (!vProc.HasExited) vProc.Kill(true); } catch { }
            if (aProc != null)
            {
                try { if (!aProc.HasExited) aProc.Kill(true); } catch { }
            }

            if (!ct.IsCancellationRequested && DateTimeOffset.Now < endAt - TimeSpan.FromSeconds(1) && vProc.HasExited && vProc.ExitCode != 0)
            {
                var err = vErr.ToString().Trim();
                if (string.IsNullOrWhiteSpace(err)) err = aErr.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(err)) throw new InvalidOperationException(err);
            }
            vProc.Dispose();
            aProc?.Dispose();
        }
    }

    static bool IsNetworkUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("srt://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase);
    }
}
