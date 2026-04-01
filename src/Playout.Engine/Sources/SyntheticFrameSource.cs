using Playout.Core.Models;
using Playout.Engine.Abstractions;
using Playout.Engine.Types;

namespace Playout.Engine.Sources;

public sealed class SyntheticFrameSource : IFrameSource
{
    readonly int _width;
    readonly int _height;
    readonly Rational _fps;
    public SyntheticFrameSource(int width, int height, Rational fps)
    {
        _width = width;
        _height = height;
        _fps = fps;
    }
    public async IAsyncEnumerable<VideoFrame> ReadFramesAsync(PlaylistItem item, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var dur = item.Duration;
        var frameDur = TimeSpan.FromSeconds((double)_fps.Den / _fps.Num);
        var total = (int)Math.Ceiling(dur.TotalSeconds * _fps.Num / _fps.Den);
        var stride = _width * 4;
        var data = new byte[_height * stride];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < total; i++)
        {
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    var off = y * stride + x * 4;
                    data[off + 0] = (byte)((x + i) % 256);
                    data[off + 1] = (byte)((y + i) % 256);
                    data[off + 2] = (byte)((x + y + i) % 256);
                    data[off + 3] = 255;
                }
            }
            yield return new VideoFrame(_width, _height, PixelFormat.BGRA, (byte[])data.Clone(), i);
            var target = TimeSpan.FromTicks(frameDur.Ticks * (i + 1));
            while (sw.Elapsed < target)
            {
                if (ct.IsCancellationRequested) yield break;
                await Task.Yield();
            }
        }
    }
}
