namespace Canvas.Models;

public sealed class Point
{
    public double X { get; set; }

    public double Y { get; set; }

    public double? Pressure { get; set; }

    public long TimeOffset { get; set; }
}
