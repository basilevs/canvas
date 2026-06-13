namespace Canvas.Dtos;

/// <summary>
/// Represents a stroke sent from the client.
/// </summary>
public sealed record StrokeInput(
    string Id,
    IReadOnlyList<PointInput> Points,
    string Color,
    float Width,
    long Duration);
