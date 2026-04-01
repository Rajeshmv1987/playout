using Playout.Engine.Types;

namespace Playout.Engine.Abstractions;

public interface IVideoSink
{
    void Send(VideoFrame frame);
}
