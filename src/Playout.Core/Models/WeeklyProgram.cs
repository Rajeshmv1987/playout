using System;

namespace Playout.Core.Models;

public sealed class WeeklyProgram
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DayOfWeek DayOfWeek { get; set; }
    public TimeSpan StartTime { get; set; }
    public string MediaPath { get; set; } = "";
    public string FileName => System.IO.Path.GetFileName(MediaPath);
    public TimeSpan Duration { get; set; }
}
