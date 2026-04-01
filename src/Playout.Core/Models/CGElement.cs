namespace Playout.Core.Models;

public enum CGElementType
{
    Logo,
    Ticker,
    LowerThird,
    Image,
    Video,
    Gif,
    Html
}

public sealed class CGElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public CGElementType Type { get; set; }
    public string Content { get; set; } = ""; // Text or File Path
    public double X { get; set; } // 0 to 1 (percentage)
    public double Y { get; set; } // 0 to 1
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsVisible { get; set; } = true;
    public string Style { get; set; } = ""; // CSS-like or JSON for animations
}
