namespace Playout.Core.Models;

public enum StartType
{
    Hard,
    Soft,
    Follow
}

public sealed class PlaylistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaId { get; set; }
    public string MediaPath { get; set; } = "";
    public string FileName { get; set; } = "";
    
    // Scheduling
    public DateTimeOffset? FixedStartUtc { get; set; }
    public StartType StartType { get; set; }
    public TimeSpan Duration { get; set; }
    public TimeSpan Padding { get; set; }
    public int SortOrder { get; set; }

    // Trimming
    public TimeSpan MarkIn { get; set; } = TimeSpan.Zero;
    public TimeSpan MarkOut { get; set; } = TimeSpan.Zero; // Zero means end of file
    
    public TimeSpan EffectiveDuration => (MarkOut == TimeSpan.Zero || MarkOut <= MarkIn) 
        ? Duration 
        : MarkOut - MarkIn;
}
