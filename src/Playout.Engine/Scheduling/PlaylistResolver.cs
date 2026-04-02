using Playout.Core.Models;

namespace Playout.Engine.Scheduling;

public sealed class PlaylistResolver
{
    readonly List<PlaylistItem> _items;

    public PlaylistResolver(IEnumerable<PlaylistItem> items)
    {
        _items = items.OrderBy(i => i.FixedStartUtc ?? DateTimeOffset.MinValue).ToList();
    }

    public bool HasItems => _items.Any();

    public void OffsetStartTimes(DateTimeOffset baseTime)
    {
        var current = baseTime;
        foreach (var item in _items)
        {
            item.FixedStartUtc = current;
            current += item.Duration + item.Padding;
        }
    }

    private readonly List<WeeklyProgram> _weeklyPrograms = new();

    public void SetWeeklyPrograms(IEnumerable<WeeklyProgram> programs)
    {
        _weeklyPrograms.Clear();
        _weeklyPrograms.AddRange(programs);
    }

    public PlaylistItem? NowOrNext(DateTimeOffset nowUtc, IEnumerable<Media>? fillers = null)
    {
        // 1. Check Main Schedule
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var start = item.FixedStartUtc ?? nowUtc;
            var dur = item.Duration;
            var end = start + dur;

            if (nowUtc >= start && nowUtc < end)
            {
                return item;
            }
        }

        // 2. Check Weekly Fixed Programs
        var localNow = nowUtc.ToLocalTime();
        var weekly = _weeklyPrograms.FirstOrDefault(w => 
            w.DayOfWeek == localNow.DayOfWeek && 
            localNow.TimeOfDay >= w.StartTime && 
            localNow.TimeOfDay < (w.StartTime + w.Duration));
        
        if (weekly != null)
        {
            return new PlaylistItem
            {
                Id = weekly.Id,
                MediaPath = weekly.MediaPath,
                FileName = weekly.FileName,
                FixedStartUtc = localNow.Date + weekly.StartTime,
                Duration = weekly.Duration,
                MarkIn = TimeSpan.Zero,
                MarkOut = weekly.Duration
            };
        }

        // 3. Gap Filler
        if (fillers != null && fillers.Any())
        {
            // Find next scheduled item to calculate gap
            var nextItem = _items.FirstOrDefault(i => i.FixedStartUtc > nowUtc);
            TimeSpan gapDuration = nextItem != null 
                ? nextItem.FixedStartUtc!.Value - nowUtc 
                : TimeSpan.FromMinutes(60); // Default long filler if no next item

            var random = new Random();
            var fillerMedia = fillers.ElementAt(random.Next(fillers.Count()));
            
            return new PlaylistItem
            {
                Id = Guid.NewGuid(),
                MediaId = fillerMedia.Id,
                MediaPath = fillerMedia.Path,
                FileName = fillerMedia.FileName,
                FixedStartUtc = nowUtc,
                StartType = StartType.Follow,
                Duration = gapDuration,
                MarkIn = TimeSpan.Zero,
                MarkOut = fillerMedia.Duration
            };
        }

        return null;
    }
}
