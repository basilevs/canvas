namespace Canvas.Dtos;

/// <summary>
/// Represents a point returned in a rendered stroke.
/// </summary>
public sealed record PointResponse(double X, double Y, double? Pressure, long TimeOffset);
