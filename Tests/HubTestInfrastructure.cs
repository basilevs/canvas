using Canvas.Dtos;
using Canvas.Hubs;
using Canvas.Models;
using Canvas.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace Canvas.Tests;

internal sealed class TestHubCallerContext : HubCallerContext
{
    private readonly DefaultHttpContext _httpContext;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public TestHubCallerContext(string connectionId, string userId)
    {
        ConnectionId = connectionId;
        UserIdentifier = userId;
        _httpContext = new DefaultHttpContext();
        _httpContext.Items["UserId"] = userId;
        Items["UserId"] = userId;
    }

    public override string ConnectionId { get; }

    public override string? UserIdentifier { get; }

    public override ClaimsPrincipal User => new(new ClaimsIdentity());

    public override IDictionary<object, object?> Items { get; } = new ConcurrentDictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => _cancellationTokenSource.Token;

    public override void Abort()
    {
        _cancellationTokenSource.Cancel();
    }
}

internal sealed class TestGroupManager : IGroupManager
{
    public List<(string ConnectionId, string GroupName, string Action)> Operations { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Operations.Add((connectionId, groupName, "add"));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Operations.Add((connectionId, groupName, "remove"));
        return Task.CompletedTask;
    }
}

internal sealed class TestWhiteboardClient : IWhiteboardClient
{
    public List<IReadOnlyList<ConnectedUserResponse>> ConnectedUsersCalls { get; } = [];

    public List<StrokeResponse> Strokes { get; } = [];

    public List<string> StrokeRemovedCalls { get; } = [];

    public List<(string UserId, string DisplayName)> UserJoinedCalls { get; } = [];

    public List<string> UserLeftCalls { get; } = [];

    public List<(string UserId, string Name)> UserRenamedCalls { get; } = [];

    public List<(string UserId, double X, double Y)> CursorMovedCalls { get; } = [];

    public Task ConnectedUsers(IReadOnlyList<ConnectedUserResponse> users)
    {
        ConnectedUsersCalls.Add(users);
        return Task.CompletedTask;
    }

    public Task StrokeReceived(StrokeResponse stroke)
    {
        Strokes.Add(stroke);
        return Task.CompletedTask;
    }

    public Task StrokeRemoved(string strokeId)
    {
        StrokeRemovedCalls.Add(strokeId);
        return Task.CompletedTask;
    }

    public Task UserJoined(string userId, string displayName)
    {
        UserJoinedCalls.Add((userId, displayName));
        return Task.CompletedTask;
    }

    public Task UserLeft(string userId)
    {
        UserLeftCalls.Add(userId);
        return Task.CompletedTask;
    }

    public Task UserRenamed(string userId, string name)
    {
        UserRenamedCalls.Add((userId, name));
        return Task.CompletedTask;
    }

    public Task CursorMoved(string userId, double x, double y)
    {
        CursorMovedCalls.Add((userId, x, y));
        return Task.CompletedTask;
    }
}

internal sealed class TestHubCallerClients : IHubCallerClients<IWhiteboardClient>
{
    public TestHubCallerClients(TestWhiteboardClient caller, TestWhiteboardClient group)
    {
        CallerClient = caller;
        GroupClient = group;
    }

    public TestWhiteboardClient CallerClient { get; }

    public TestWhiteboardClient GroupClient { get; }

    public IWhiteboardClient All => GroupClient;

    public IWhiteboardClient Caller => CallerClient;

    public IWhiteboardClient Others => GroupClient;

    public IWhiteboardClient Client(string connectionId) => GroupClient;

    public IWhiteboardClient Clients(IReadOnlyList<string> connectionIds) => GroupClient;

    public IWhiteboardClient Group(string groupName, params string[] excludedConnectionIds) => GroupClient;

    public IWhiteboardClient Group(string groupName, IReadOnlyList<string> excludedConnectionIds) => GroupClient;

    public IWhiteboardClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => GroupClient;

    public IWhiteboardClient Groups(IReadOnlyList<string> groupNames) => GroupClient;

    public IWhiteboardClient Groups(IReadOnlyList<string> groupNames, IReadOnlyList<string> excludedConnectionIds) => GroupClient;

    public IWhiteboardClient User(string userId) => GroupClient;

    public IWhiteboardClient Users(IReadOnlyList<string> userIds) => GroupClient;

    public IWhiteboardClient OthersInGroup(string groupName) => GroupClient;

    public IWhiteboardClient OthersInGroup(string groupName, params string[] excludedConnectionIds) => GroupClient;

    public IWhiteboardClient OthersInGroup(string groupName, IReadOnlyList<string> excludedConnectionIds) => GroupClient;

    public IWhiteboardClient Group(string groupName) => GroupClient;

    public IWhiteboardClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => GroupClient;
}

internal sealed class InMemoryBoardService : IBoardService
{
    private readonly Dictionary<string, Board> _boards = new(StringComparer.Ordinal);

    public Task<Board> CreateBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        var board = new Board
        {
            Id = boardId,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };

        _boards[boardId] = board;
        return Task.FromResult(board);
    }

    public Task<Board?> GetBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        _boards.TryGetValue(boardId, out var board);
        return Task.FromResult(board);
    }

    public Task<Board> GetOrCreateBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        if (_boards.TryGetValue(boardId, out var board))
        {
            return Task.FromResult(board);
        }

        return CreateBoardAsync(boardId, cancellationToken);
    }

    public Task UpdateLastActivityAsync(string boardId, CancellationToken cancellationToken)
    {
        if (!_boards.TryGetValue(boardId, out var board))
        {
            throw new KeyNotFoundException(boardId);
        }

        board.LastActivityAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryStrokeEventService : IStrokeEventService
{
    private readonly List<StrokeEvent> _events = [];
    private long _sequence;

    public IReadOnlyList<StrokeEvent> Events => _events;

    public Task<bool> AppendEventAsync(string boardId, EventType type, Stroke stroke, CancellationToken cancellationToken)
    {
        if (type == EventType.Add &&
            _events.Any(e => e.BoardId == boardId && e.Type == EventType.Add && e.Stroke.Id == stroke.Id))
        {
            return Task.FromResult(false);
        }

        // Monotonic, distinct timestamps keep ordering deterministic in tests.
        _events.Add(new StrokeEvent
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            BoardId = boardId,
            Type = type,
            Stroke = stroke,
            Timestamp = DateTime.UtcNow.AddTicks(_sequence++)
        });

        return Task.FromResult(true);
    }

    public Task<StrokeEventPage> GetEventsPageAsync(string boardId, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var ordered = OrderedFor(boardId);
        var totalEvents = ordered.Count;
        var totalPages = (int)((totalEvents + (long)pageSize - 1) / pageSize);
        var slice = ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new StrokeEventPage(slice, totalEvents, totalPages));
    }

    public Task<IReadOnlyList<StrokeEvent>> GetEventsSinceAsync(string boardId, DateTime sinceTimestamp, CancellationToken cancellationToken)
    {
        IReadOnlyList<StrokeEvent> result = OrderedFor(boardId)
            .Where(e => e.Timestamp >= sinceTimestamp)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<Stroke?> GetLastRemovableStrokeByUserAsync(string boardId, string userId, CancellationToken cancellationToken)
    {
        foreach (var add in OrderedFor(boardId)
                     .Where(e => e.Type == EventType.Add && e.Stroke.UserId == userId)
                     .Reverse())
        {
            var removed = _events.Any(e => e.BoardId == boardId && e.Type == EventType.Remove && e.Stroke.Id == add.Stroke.Id);
            if (!removed)
            {
                return Task.FromResult<Stroke?>(add.Stroke);
            }
        }

        return Task.FromResult<Stroke?>(null);
    }

    private List<StrokeEvent> OrderedFor(string boardId)
    {
        return _events
            .Where(e => e.BoardId == boardId)
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.Stroke.Id, StringComparer.Ordinal)
            .ToList();
    }
}

internal sealed class InMemoryUserProfileService : IUserProfileService
{
    private readonly Dictionary<string, UserProfile> _profiles = new(StringComparer.Ordinal);

    public Task<UserProfile> GetOrCreateProfileAsync(string userId, CancellationToken cancellationToken)
    {
        if (_profiles.TryGetValue(userId, out var profile))
        {
            return Task.FromResult(profile);
        }

        profile = new UserProfile
        {
            UserId = userId,
            DisplayName = "Anonymous",
            CreatedAt = DateTime.UtcNow
        };

        _profiles[userId] = profile;
        return Task.FromResult(profile);
    }

    public async Task SetDisplayNameAsync(string userId, string name, CancellationToken cancellationToken)
    {
        var profile = await GetOrCreateProfileAsync(userId, cancellationToken);
        profile.DisplayName = name;
    }

    public Task<string?> GetDisplayNameAsync(string userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_profiles.TryGetValue(userId, out var profile) ? profile.DisplayName : null);
    }

    public Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(IEnumerable<string> userIds, CancellationToken cancellationToken)
    {
        var result = userIds
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, id => _profiles.TryGetValue(id, out var profile) ? profile.DisplayName : "Anonymous", StringComparer.Ordinal);

        return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
    }

    public async Task SetLastBoardAsync(string userId, string boardName, CancellationToken cancellationToken)
    {
        var profile = await GetOrCreateProfileAsync(userId, cancellationToken);
        profile.LastBoardName = boardName;
    }

    public Task<string?> GetLastBoardAsync(string userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(_profiles.TryGetValue(userId, out var profile) ? profile.LastBoardName : null);
    }

    public Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
