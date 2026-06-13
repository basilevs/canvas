namespace Canvas.Dtos;

/// <summary>
/// Represents the board snapshot sent to a joining client.
/// </summary>
public sealed record BoardSnapshotResponse(string BoardName, IReadOnlyList<StrokeResponse> ActiveStrokes);
