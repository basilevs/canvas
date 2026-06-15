namespace Canvas.Models;

public sealed class Stroke
{
    public string Id { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public List<Point> Points { get; set; } = [];

    public string Color { get; set; } = string.Empty;

    public float Width { get; set; }

    public DateTime Timestamp { get; set; }
}
