using Canvas.Dtos;

namespace Canvas.Hubs;

public interface IWhiteboardClient
{
    Task LoadSnapshot(BoardSnapshotResponse board, IReadOnlyList<ConnectedUserResponse> users);

    Task StrokeReceived(StrokeResponse stroke);

    Task UserJoined(string userId, string displayName);

    Task UserLeft(string userId);

    Task UserRenamed(string userId, string name);

    Task CursorMoved(string userId, double x, double y);
}
