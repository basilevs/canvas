namespace Canvas.Dtos;

/// <summary>
/// Returned to the joining caller so the client can reflect its own identity,
/// last known display name, and the board's established aspect ratio.
/// </summary>
public sealed record JoinBoardResponse(string UserId, string DisplayName, double AspectRatio);
