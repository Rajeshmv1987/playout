using Playout.Core.Models;

namespace Playout.Core.Services;

public sealed class AdScheduler
{
    private readonly List<Media> _adPool = new();
    private readonly object _lock = new();

    public void UpdateAdPool(IEnumerable<Media> ads)
    {
        lock (_lock)
        {
            _adPool.Clear();
            _adPool.AddRange(ads.Where(m => m.Category == "Ads"));
        }
    }

    public List<PlaylistItem> GetAdsForSlot(DateTimeOffset slotTime, int count = 2)
    {
        lock (_lock)
        {
            if (!_adPool.Any()) return new List<PlaylistItem>();

            var result = new List<PlaylistItem>();
            var random = new Random();
            var current = slotTime;

            for (int i = 0; i < count; i++)
            {
                var ad = _adPool[random.Next(_adPool.Count)];
                result.Add(new PlaylistItem
                {
                    Id = Guid.NewGuid(),
                    MediaId = ad.Id,
                    MediaPath = ad.Path,
                    FileName = ad.FileName,
                    FixedStartUtc = current,
                    StartType = StartType.Hard,
                    Duration = ad.Duration,
                    MarkIn = TimeSpan.Zero,
                    MarkOut = ad.Duration
                });
                current += ad.Duration;
            }
            return result;
        }
    }
}
