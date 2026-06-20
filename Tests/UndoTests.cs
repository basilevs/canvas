using Canvas.Dtos;
using Canvas.Hubs;
using Canvas.Models;
using Canvas.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Canvas.Tests;

// Integration coverage for undo (TASK-041): exercises the hub against a real
// StrokeEventService backed by a throwaway MongoDB database so the index-backed
// GetLastRemovableStrokeByUserAsync query is verified end-to-end. The board and
// profile services are in-memory fakes — only the event log needs to be real.
[TestClass]
public sealed class UndoTests
{
    private const string BoardName = "undo-board";

    private MongoDbContext _context = null!;
    private IMongoClient _client = null!;
    private string _databaseName = null!;
    private StrokeEventService _strokeEvents = null!;
    private InMemoryBoardService _boardService = null!;
    private InMemoryUserProfileService _userProfiles = null!;
    private TestWhiteboardClient _group = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        (_context, _client, _databaseName) = await MongoTestSupport.CreateContextAsync(TestContext.CancellationTokenSource.Token);
        _strokeEvents = new StrokeEventService(_context);
        _boardService = new InMemoryBoardService();
        _userProfiles = new InMemoryUserProfileService();
        _group = new TestWhiteboardClient();
    }

    [TestCleanup]
    public async Task TearDownAsync()
    {
        if (_client is not null && _databaseName is not null)
        {
            await MongoTestSupport.DropDatabaseAsync(_client, _databaseName);
        }
    }

    [TestMethod]
    public async Task UndoLastStroke_removes_only_callers_most_recent_stroke_and_broadcasts()
    {
        var (hub, caller) = await CreateJoinedHubAsync("user-1");

        var first = await DrawAsync(hub);
        var second = await DrawAsync(hub);

        await hub.UndoLastStroke(BoardName);

        // The newest stroke is the one removed.
        Assert.AreEqual(second, _group.StrokeRemovedCalls.Single());

        // A Remove event is appended to the authoritative log for the newest stroke only.
        var events = await GetAllEventsAsync();
        Assert.AreEqual(1, events.Count(e => e.Type == EventType.Remove));
        Assert.AreEqual(second, events.Single(e => e.Type == EventType.Remove).Stroke.Id);

        // The older stroke is now the next removable one.
        var remaining = await _strokeEvents.GetLastRemovableStrokeByUserAsync(BoardId, "user-1", default);
        Assert.IsNotNull(remaining);
        Assert.AreEqual(first, remaining.Id);
    }

    [TestMethod]
    public async Task UndoLastStroke_removes_strokes_in_reverse_draw_order()
    {
        var (hub, _) = await CreateJoinedHubAsync("user-1");

        var first = await DrawAsync(hub);
        var second = await DrawAsync(hub);
        var third = await DrawAsync(hub);

        await hub.UndoLastStroke(BoardName);
        await hub.UndoLastStroke(BoardName);
        await hub.UndoLastStroke(BoardName);

        CollectionAssert.AreEqual(
            new[] { third, second, first },
            _group.StrokeRemovedCalls.ToArray());

        var removable = await _strokeEvents.GetLastRemovableStrokeByUserAsync(BoardId, "user-1", default);
        Assert.IsNull(removable);
    }

    [TestMethod]
    public async Task UndoLastStroke_is_noop_when_caller_has_no_strokes()
    {
        var (hub, _) = await CreateJoinedHubAsync("user-1");

        await hub.UndoLastStroke(BoardName);

        Assert.AreEqual(0, _group.StrokeRemovedCalls.Count);
        var events = await GetAllEventsAsync();
        Assert.AreEqual(0, events.Count(e => e.Type == EventType.Remove));
    }

    [TestMethod]
    public async Task UndoLastStroke_never_removes_another_users_stroke()
    {
        var (aliceHub, _) = await CreateJoinedHubAsync("alice");
        var (bobHub, _) = await CreateJoinedHubAsync("bob");

        var aliceStroke = await DrawAsync(aliceHub);
        var bobStroke = await DrawAsync(bobHub);

        // Alice undoes: only her stroke is affected even though Bob's is newer.
        await aliceHub.UndoLastStroke(BoardName);

        Assert.AreEqual(aliceStroke, _group.StrokeRemovedCalls.Single());

        var events = await GetAllEventsAsync();
        Assert.AreEqual(aliceStroke, events.Single(e => e.Type == EventType.Remove).Stroke.Id);

        // Bob's stroke is untouched and remains his last removable stroke.
        var bobRemovable = await _strokeEvents.GetLastRemovableStrokeByUserAsync(BoardId, "bob", default);
        Assert.IsNotNull(bobRemovable);
        Assert.AreEqual(bobStroke, bobRemovable.Id);

        // Alice now has nothing left to undo.
        var aliceRemovable = await _strokeEvents.GetLastRemovableStrokeByUserAsync(BoardId, "alice", default);
        Assert.IsNull(aliceRemovable);
    }

    private static string BoardId =>
        BoardNameNormalizer.TryNormalizeBoardName(BoardName, out var boardId)
            ? boardId
            : throw new InvalidOperationException("Board name failed to normalize.");

    private async Task<(WhiteboardHub Hub, TestWhiteboardClient Caller)> CreateJoinedHubAsync(string userId)
    {
        var caller = new TestWhiteboardClient();
        var context = new TestHubCallerContext("conn-" + Guid.NewGuid().ToString("N"), userId);

        var hub = new WhiteboardHub(_boardService, _userProfiles, _strokeEvents, NullLogger<WhiteboardHub>.Instance)
        {
            Context = context,
            Clients = new TestHubCallerClients(caller, _group),
            Groups = new TestGroupManager()
        };

        await hub.JoinBoard(BoardName, DateTime.UnixEpoch);
        return (hub, caller);
    }

    private static async Task<string> DrawAsync(WhiteboardHub hub)
    {
        var stroke = new StrokeInput(
            Guid.NewGuid().ToString("D"),
            [new PointInput(1, 2, null, 0)],
            "#123456",
            3);

        await hub.SendStroke(BoardName, stroke);

        // Space appends out so server-assigned timestamps establish a strict order.
        await Task.Delay(15);
        return stroke.Id;
    }

    private async Task<IReadOnlyList<StrokeEvent>> GetAllEventsAsync()
    {
        var page = await _strokeEvents.GetEventsPageAsync(BoardId, 1, StrokeEventService.DefaultPageSize, default);
        return page.Events;
    }
}
