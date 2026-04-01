using Playout.Core.Models;
using Playout.Engine;
using Playout.Engine.Sinks;
using Playout.Engine.Sources;
using Playout.Engine.Types;

var now = DateTimeOffset.UtcNow;
var items = new List<PlaylistItem>
{
    new PlaylistItem
    {
        Id = Guid.NewGuid(),
        MediaPath = "synthetic",
        FixedStartUtc = now,
        StartType = StartType.Hard,
        Duration = TimeSpan.FromSeconds(10),
        Padding = TimeSpan.FromMilliseconds(0)
    },
    new PlaylistItem
    {
        Id = Guid.NewGuid(),
        MediaPath = "synthetic",
        FixedStartUtc = now.AddSeconds(10),
        StartType = StartType.Hard,
        Duration = TimeSpan.FromSeconds(10),
        Padding = TimeSpan.FromMilliseconds(0)
    }
};

var src = new SyntheticFrameSource(1280, 720, new Rational(30000, 1001));
var sink = new ConsoleSink();
var engine = new PlayoutEngine(src, sink, items);
using var cts = new CancellationTokenSource();
var run = engine.RunAsync(cts.Token);
Console.WriteLine("Running. Press Enter to stop.");
Console.ReadLine();
cts.Cancel();
await run;
