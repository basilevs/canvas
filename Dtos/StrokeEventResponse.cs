namespace Canvas.Dtos;

/// <summary>
/// Represents a single append-only history event returned to clients: an
/// <c>Add</c> or <c>Remove</c> of a stroke at a server-assigned UTC timestamp.
/// </summary>
public sealed record StrokeEventResponse(
    string Id,
    string Type,
    StrokeResponse Stroke,
    DateTime Timestamp);
