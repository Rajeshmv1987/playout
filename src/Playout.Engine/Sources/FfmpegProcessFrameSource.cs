using System.Diagnostics;
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

        var vPsi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -nostdin {seekArg} -i \"{item.MediaPath}\" {durationArg} -vf scale={_width}:{_height},fps={_fps.Num}/{_fps.Den} -pix_fmt bgra -f rawvideo -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var aPsi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -nostdin {seekArg} -i \"{item.MediaPath}\" {durationArg} -af aresample={sampleRate},pan=stereo|c0=c0|c1=c1 -f f32le -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var vProc = Process.Start(vPsi);
        using var aProc = Process.Start(aPsi);

        if (vProc == null || aProc == null) yield break;

        // Error reading loops to prevent pipe blocks
        _ = Task.Run(async () => { while (!vProc.StandardError.EndOfStream) await vProc.StandardError.ReadLineAsync(ct); }, ct);
        _ = Task.Run(async () => { while (!aProc.StandardError.EndOfStream) await aProc.StandardError.ReadLineAsync(ct); }, ct);

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

                // Read audio
                int aRead = 0;
                while (aRead < audioBytes)
                {
                    int n = await aProc.StandardOutput.BaseStream.ReadAsync(audioBuffer.AsMemory(aRead, audioBytes - aRead), ct);
                    if (n <= 0) break; // Audio might end early, continue with silence if needed
                    aRead += n;
                }

                float[]? audioData = null;
                if (aRead > 0)
                {
                    audioData = new float[aRead / 4];
                    Buffer.BlockCopy(audioBuffer, 0, audioData, 0, aRead);
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
            try { if (!aProc.HasExited) aProc.Kill(true); } catch { }
            vProc.Dispose();
            aProc.Dispose();
        }
    }
}
