namespace Canvas.Dtos;

/// <summary>
/// Represents a stroke returned to clients.
/// </summary>
public sealed record StrokeResponse(
    string Id,
    string UserId,
    string Color,
    float Width,
    IReadOnlyList<PointResponse> Points,
    DateTime Timestamp);
