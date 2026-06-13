namespace Canvas.Dtos;

/// <summary>
/// Represents a connected user in a board roster.
/// </summary>
public sealed record ConnectedUserResponse(string UserId, string DisplayName);
