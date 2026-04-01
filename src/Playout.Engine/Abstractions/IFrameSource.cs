using Playout.Core.Models;
using Playout.Engine.Types;

namespace Playout.Engine.Abstractions;

public interface IFrameSource
{
    IAsyncEnumerable<VideoFrame> ReadFramesAsync(PlaylistItem item, CancellationToken ct);
}
