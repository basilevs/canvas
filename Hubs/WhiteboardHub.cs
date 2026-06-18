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
    private readonly IStrokeEventService _strokeEventService;
    private readonly ILogger<WhiteboardHub> _logger;

    public WhiteboardHub(
        IBoardService boardService,
        IUserProfileService userProfileService,
        IStrokeEventService strokeEventService,
        ILogger<WhiteboardHub> logger)
    {
        _boardService = boardService;
        _userProfileService = userProfileService;
        _strokeEventService = strokeEventService;
        _logger = logger;
    }

    public async Task JoinBoard(string boardName, DateTime sinceTimestamp)
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

        // Subscribe to live broadcasts FIRST, then replay the missed tail to the
        // caller through the same typed channel. The inclusive `>=` tail read may
        // overlap with a live broadcast that arrives in between; the client
        // de-duplicates by stroke Id, so no event is missed or duplicated.
        await Groups.AddToGroupAsync(Context.ConnectionId, boardId, cancellationToken);
        Connections[Context.ConnectionId] = new UserConnection(boardId, userId);

        var connectedUsers = await GetConnectedUsersAsync(boardId, cancellationToken);
        await Clients.Caller.ConnectedUsers(connectedUsers);

        var tail = await _strokeEventService.GetEventsSinceAsync(boardId, sinceTimestamp, cancellationToken);
        foreach (var strokeEvent in tail)
        {
            if (strokeEvent.Type == EventType.Remove)
            {
                await Clients.Caller.StrokeRemoved(strokeEvent.Stroke.Id);
            }
            else
            {
                await Clients.Caller.StrokeReceived(ToStrokeResponse(strokeEvent.Stroke));
            }
        }

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
            Timestamp = DateTime.UtcNow,
            Points = stroke.Points.Select(point => new Point
            {
                X = point.X,
                Y = point.Y,
                Pressure = point.Pressure,
                TimeOffset = point.TimeOffset
            }).ToList()
        };

        var appended = await _strokeEventService.AppendEventAsync(connection.BoardId, EventType.Add, persistedStroke, cancellationToken);
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

    public async Task UndoLastStroke(string boardName)
    {
        var cancellationToken = Context.ConnectionAborted;
        var userId = GetUserId();
        var connection = GetConnection(boardName);

        var stroke = await _strokeEventService.GetLastRemovableStrokeByUserAsync(
            connection.BoardId,
            userId,
            cancellationToken);
        if (stroke is null)
        {
            return;
        }

        await _strokeEventService.AppendEventAsync(connection.BoardId, EventType.Remove, stroke, cancellationToken);
        await Clients.Group(connection.BoardId).StrokeRemoved(stroke.Id);
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
        // SignalR drops a connection on protocol-level failures (e.g. a stroke
        // payload exceeding MaximumReceiveMessageSize) before any hub method is
        // dispatched, surfacing the cause only here at Debug. Log it ourselves so
        // these disconnects are visible without enabling SignalR Debug logging.
        if (exception is not null)
        {
            _logger.LogError(
                exception,
                "Connection {ConnectionId} disconnected with an error.",
                Context.ConnectionId);
        }

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
