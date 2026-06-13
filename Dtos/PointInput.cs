namespace Canvas.Dtos;

/// <summary>
/// Represents a point sent from the client when drawing a stroke.
/// </summary>
public sealed record PointInput(double X, double Y, double? Pressure, long TimeOffset);
