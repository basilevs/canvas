using Canvas.Dtos;
using Canvas.Models;
using Canvas.Services;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Canvas.Hubs;

public sealed class WhiteboardHub : Hub<IWhiteboardClient>
{
    private static readonly ConcurrentDictionary<string, UserConnection> Connections = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> LastActivityRefreshes = new(StringComparer.Ordinal);
    private static readonly TimeSpan LastActivityRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IBoardService _boardService;
    private readonly IUserProfileService _userProfileService;

    public WhiteboardHub(IBoardService boardService, IUserProfileService userProfileService)
    {
        _boardService = boardService;
        _userProfileService = userProfileService;
    }

    public async Task JoinBoard(string boardName)
    {
        var cancellationToken = Context.ConnectionAborted;
        var userId = GetUserId();
        if (!BoardNameNormalizer.TryNormalizeBoardName(boardName, out var boardId))
        {
            throw new HubException("Invalid board name.");
        }

        var profileTask = _userProfileService.GetOrCreateProfileAsync(userId, cancellationToken);
        var boardTask = _boardService.GetOrCreateBoardAsync(boardId, cancellationToken);
        await Task.WhenAll(profileTask, boardTask);

        var profile = await profileTask;
        await _userProfileService.SetLastBoardAsync(userId, boardId, cancellationToken);
        await _boardService.UpdateLastActivityAsync(boardId, cancellationToken);
        LastActivityRefreshes[boardId] = DateTime.UtcNow;

        if (Connections.TryGetValue(Context.ConnectionId, out var existingConnection) &&
            !string.Equals(existingConnection.BoardId, boardId, StringComparison.Ordinal))
        {
            await RemoveConnectionAsync(Context.ConnectionId, existingConnection, broadcastLeft: true);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, boardId, cancellationToken);
        Connections[Context.ConnectionId] = new UserConnection(boardId, userId);

        var connectedUsers = await GetConnectedUsersAsync(boardId, cancellationToken);
        var strokes = await _boardService.GetSnapshotAsync(boardId, cancellationToken);

        var board = new BoardSnapshotResponse(
            boardId,
            strokes.Select(ToStrokeResponse).ToList());

        await Clients.Caller.LoadSnapshot(board, connectedUsers);
        await Clients.OthersInGroup(boardId).UserJoined(userId, profile.DisplayName);
    }

    public async Task LeaveBoard(string boardName)
    {
        if (!BoardNameNormalizer.TryNormalizeBoardName(boardName, out var boardId))
        {
            throw new HubException("Invalid board name.");
        }

        if (!Connections.TryGetValue(Context.ConnectionId, out var connection) ||
            !string.Equals(connection.BoardId, boardId, StringComparison.Ordinal))
        {
            return;
        }

        await RemoveConnectionAsync(Context.ConnectionId, connection, broadcastLeft: true, removeFromGroup: true);
    }

    public async Task SendStroke(string boardName, StrokeInput stroke)
    {
        var cancellationToken = Context.ConnectionAborted;
        var userId = GetUserId();
        var connection = GetConnection(boardName);

        if (stroke is null)
        {
            throw new HubException("Stroke is required.");
        }

        if (!Guid.TryParse(stroke.Id, out var parsedStrokeId))
        {
            throw new HubException("Invalid stroke id.");
        }

        if (stroke.Points is null)
        {
            throw new HubException("Stroke points are required.");
        }

        var persistedStroke = new Stroke
        {
            Id = parsedStrokeId.ToString("D"),
            UserId = userId,
            Color = stroke.Color,
            Width = stroke.Width,
            Duration = stroke.Duration,
            Timestamp = DateTime.UtcNow,
            Points = stroke.Points.Select(point => new Point
            {
                X = point.X,
                Y = point.Y,
                Pressure = point.Pressure,
                TimeOffset = point.TimeOffset
            }).ToList()
        };

        var appended = await _boardService.AddStrokeToSnapshotAsync(connection.BoardId, persistedStroke, cancellationToken);
        if (!appended)
        {
            return;
        }

        await Clients.Group(connection.BoardId).StrokeReceived(ToStrokeResponse(persistedStroke));
        if (ShouldRefreshLastActivity(connection.BoardId))
        {
            await _boardService.UpdateLastActivityAsync(connection.BoardId, cancellationToken);
            LastActivityRefreshes[connection.BoardId] = DateTime.UtcNow;
        }
    }

    public async Task SetDisplayName(string name)
    {
        var cancellationToken = Context.ConnectionAborted;
        var userId = GetUserId();
        var displayName = NormalizeDisplayName(name);

        await _userProfileService.SetDisplayNameAsync(userId, displayName, cancellationToken);

        if (Connections.TryGetValue(Context.ConnectionId, out var connection))
        {
            await Clients.Group(connection.BoardId).UserRenamed(userId, displayName);
        }
    }

    public async Task MoveCursor(string boardName, double x, double y)
    {
        if (!BoardNameNormalizer.TryNormalizeBoardName(boardName, out var boardId))
        {
            throw new HubException("Invalid board name.");
        }

        if (!Connections.TryGetValue(Context.ConnectionId, out var connection) ||
            !string.Equals(connection.BoardId, boardId, StringComparison.Ordinal))
        {
            return;
        }

        await Clients.OthersInGroup(connection.BoardId).CursorMoved(connection.UserId, x, y);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Connections.TryRemove(Context.ConnectionId, out var connection))
        {
            await Clients.Group(connection.BoardId).UserLeft(connection.UserId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task RemoveConnectionAsync(string connectionId, UserConnection connection, bool broadcastLeft, bool removeFromGroup = true)
    {
        Connections.TryRemove(connectionId, out _);

        if (removeFromGroup)
        {
            await Groups.RemoveFromGroupAsync(connectionId, connection.BoardId, Context.ConnectionAborted);
        }

        if (broadcastLeft)
        {
            await Clients.Group(connection.BoardId).UserLeft(connection.UserId);
        }
    }

    private async Task<IReadOnlyList<ConnectedUserResponse>> GetConnectedUsersAsync(
        string boardId,
        CancellationToken cancellationToken)
    {
        var userIds = Connections.Values
            .Where(connection => string.Equals(connection.BoardId, boardId, StringComparison.Ordinal))
            .Select(connection => connection.UserId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var displayNames = await _userProfileService.GetDisplayNamesAsync(userIds, cancellationToken);
        return userIds.Select(userId => new ConnectedUserResponse(userId, displayNames[userId])).ToList();
    }

    private UserConnection GetConnection(string boardName)
    {
        if (!BoardNameNormalizer.TryNormalizeBoardName(boardName, out var boardId))
        {
            throw new HubException("Invalid board name.");
        }

        if (!Connections.TryGetValue(Context.ConnectionId, out var connection) ||
            !string.Equals(connection.BoardId, boardId, StringComparison.Ordinal))
        {
            throw new HubException("You must join the board first.");
        }

        return connection;
    }

    private string GetUserId()
    {
        if (Context.Items.TryGetValue("UserId", out var itemValue) && itemValue is string itemUserId)
        {
            return itemUserId;
        }

        var httpContext = Context.GetHttpContext()
            ?? throw new HubException("User identity is not available.");

        return httpContext.Items["UserId"] as string
            ?? throw new HubException("User identity is not available.");
    }

    private static bool ShouldRefreshLastActivity(string boardId)
    {
        var now = DateTime.UtcNow;
        if (!LastActivityRefreshes.TryGetValue(boardId, out var lastRefresh))
        {
            return true;
        }

        return now - lastRefresh >= LastActivityRefreshInterval;
    }

    private static StrokeResponse ToStrokeResponse(Stroke stroke)
    {
        return new StrokeResponse(
            stroke.Id,
            stroke.UserId,
            stroke.Color,
            stroke.Width,
            stroke.Points.Select(point => new PointResponse(
                point.X,
                point.Y,
                point.Pressure,
                point.TimeOffset)).ToList(),
            stroke.Timestamp);
    }

    private static string NormalizeDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new HubException("Display name is invalid.");
        }

        var displayName = name.Trim();
        if (displayName.Length > 30)
        {
            throw new HubException("Display name is invalid.");
        }

        return displayName;
    }

    private sealed record UserConnection(string BoardId, string UserId);
}
