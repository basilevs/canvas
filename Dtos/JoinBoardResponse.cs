namespace Canvas.Dtos;

/// <summary>
/// Returned to the joining caller so the client can reflect its own identity and
/// last known display name.
/// </summary>
public sealed record JoinBoardResponse(string UserId, string DisplayName);
