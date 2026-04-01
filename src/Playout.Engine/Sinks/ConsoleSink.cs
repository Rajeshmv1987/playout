using Playout.Engine.Abstractions;
using Playout.Engine.Types;

namespace Playout.Engine.Sinks;

public sealed class ConsoleSink : IVideoSink
{
    long _lastReportTicks;
    long _count;
    public void Send(VideoFrame frame)
    {
        _count++;
        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - _lastReportTicks > TimeSpan.TicksPerSecond)
        {
            _lastReportTicks = nowTicks;
            Console.WriteLine($"Frames { _count } W{frame.Width} H{frame.Height}");
        }
    }
}
