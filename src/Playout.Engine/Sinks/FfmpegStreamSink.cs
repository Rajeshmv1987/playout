using System.Diagnostics;
using Playout.Engine.Abstractions;
using Playout.Engine.Types;

namespace Playout.Engine.Sinks;

public sealed class FfmpegStreamSink : IVideoSink, IDisposable
{
    private readonly Process _ffmpegProcess;
    private readonly int _width;
    private readonly int _height;

    public FfmpegStreamSink(string streamUrl, int width, int height, Rational fps)
    {
        _width = width;
        _height = height;

        var fpsStr = $"{fps.Num}/{fps.Den}";
        
        // Example: ffmpeg -f rawvideo -pix_fmt bgra -s 1280x720 -r 30000/1001 -i - 
        // -f f32le -ar 48000 -ac 2 -i - 
        // -c:v libx264 -preset veryfast -maxrate 3000k -bufsize 6000k -pix_fmt yuv420p -g 60 -c:a aac -f flv rtmp://...
        
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -f rawvideo -pix_fmt bgra -s {width}x{height} -r {fpsStr} -i - " +
                        $"-f f32le -ar 48000 -ac 2 -i - " +
                        $"-c:v libx264 -preset veryfast -maxrate 3000k -bufsize 6000k -pix_fmt yuv420p -g 60 " +
                        $"-c:a aac -b:a 128k -f flv \"{streamUrl}\"",
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _ffmpegProcess = Process.Start(psi) ?? throw new Exception("Failed to start FFmpeg for streaming.");
    }

    public void Send(VideoFrame frame)
    {
        try
        {
            // Write video
            _ffmpegProcess.StandardInput.BaseStream.Write(frame.Data, 0, frame.Data.Length);
            
            // Write audio if available
            if (frame.AudioData != null)
            {
                byte[] audioBytes = new byte[frame.AudioData.Length * 4];
                Buffer.BlockCopy(frame.AudioData, 0, audioBytes, 0, audioBytes.Length);
                _ffmpegProcess.StandardInput.BaseStream.Write(audioBytes, 0, audioBytes.Length);
            }
            
            _ffmpegProcess.StandardInput.BaseStream.Flush();
        }
        catch
        {
            // FFmpeg might have closed the pipe
        }
    }

    public void Dispose()
    {
        try
        {
            _ffmpegProcess.StandardInput.BaseStream.Close();
            _ffmpegProcess.WaitForExit(1000);
            if (!_ffmpegProcess.HasExited) _ffmpegProcess.Kill();
            _ffmpegProcess.Dispose();
        }
        catch { }
    }
}
