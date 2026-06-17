using Canvas.Dtos;

namespace Canvas.Hubs;

public interface IWhiteboardClient
{
    Task ConnectedUsers(IReadOnlyList<ConnectedUserResponse> users);

    Task StrokeReceived(StrokeResponse stroke);

    Task StrokeRemoved(string strokeId);

    Task UserJoined(string userId, string displayName);

    Task UserLeft(string userId);

    Task UserRenamed(string userId, string name);

    Task CursorMoved(string userId, double x, double y);
}
