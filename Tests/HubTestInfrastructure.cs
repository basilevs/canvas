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
    public List<BoardSnapshotResponse> Snapshots { get; } = [];

    public List<StrokeResponse> Strokes { get; } = [];

    public List<(string UserId, string DisplayName)> UserJoinedCalls { get; } = [];

    public List<string> UserLeftCalls { get; } = [];

    public List<(string UserId, string Name)> UserRenamedCalls { get; } = [];

    public List<(string UserId, double X, double Y)> CursorMovedCalls { get; } = [];

    public Task LoadSnapshot(BoardSnapshotResponse board, IReadOnlyList<ConnectedUserResponse> users)
    {
        Snapshots.Add(board);
        return Task.CompletedTask;
    }

    public Task StrokeReceived(StrokeResponse stroke)
    {
        Strokes.Add(stroke);
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
            LastActivityAt = DateTime.UtcNow,
            ActiveStrokes = []
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

    public Task<IReadOnlyList<Stroke>> GetSnapshotAsync(string boardId, CancellationToken cancellationToken)
    {
        _boards.TryGetValue(boardId, out var board);
        return Task.FromResult<IReadOnlyList<Stroke>>(board?.ActiveStrokes.ToList() ?? []);
    }

    public Task<bool> AddStrokeToSnapshotAsync(string boardId, Stroke stroke, CancellationToken cancellationToken)
    {
        if (!_boards.TryGetValue(boardId, out var board))
        {
            throw new KeyNotFoundException(boardId);
        }

        if (board.ActiveStrokes.Any(existing => existing.Id == stroke.Id))
        {
            return Task.FromResult(false);
        }

        board.ActiveStrokes.Add(stroke);
        return Task.FromResult(true);
    }

    public Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
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
