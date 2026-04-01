namespace Playout.Core.Models;

public sealed class Media
{
    public Guid Id { get; init; }
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int FpsNum { get; set; }
    public int FpsDen { get; set; }
    public string Category { get; set; } = "Program"; // Program, Ad, Filler, etc.
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
