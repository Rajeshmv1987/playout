namespace Playout.Core.Models;

public enum ScheduleStatus
{
    Pending,
    Playing,
    Completed,
    Failed,
    Skipped
}

public sealed class Schedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime ScheduledDate { get; set; }
    public DateTimeOffset StartTimeUtc { get; set; }
    public ScheduleStatus Status { get; set; } = ScheduleStatus.Pending;
    public bool IsLoop { get; set; }
    public List<PlaylistItem> Items { get; set; } = new();
}
