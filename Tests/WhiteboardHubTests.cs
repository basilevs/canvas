using Canvas.Dtos;
using Canvas.Hubs;
using Canvas.Models;
using Canvas.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Canvas.Tests;

[TestClass]
public sealed class WhiteboardHubTests
{
    [TestMethod]
    public async Task JoinBoard_sends_roster_and_broadcasts_user_joined()
    {
        var hub = CreateHub(out var caller, out var group, out var boardRepository, out var userProfileRepository, out _, out var groups, out _);
        await boardRepository.CreateBoardAsync("demo-board", default);
        await userProfileRepository.SetDisplayNameAsync("user-1", "Alice", default);

        await hub.JoinBoard("Demo-Board", DateTime.UnixEpoch);

        Assert.HasCount(1, caller.ConnectedUsersCalls);
        Assert.IsEmpty(caller.Strokes);
        Assert.HasCount(1, group.UserJoinedCalls);
        Assert.AreEqual(("user-1", "Alice"), group.UserJoinedCalls[0]);
        Assert.IsTrue(groups.Operations.Any(operation => operation.Action == "add" && operation.GroupName == "demo-board"));
    }

    [TestMethod]
    public async Task JoinBoard_replays_event_tail_to_caller()
    {
        var hub = CreateHub(out var caller, out _, out var boardRepository, out _, out _, out _, out var strokeEvents);
        await boardRepository.CreateBoardAsync("demo-board", default);
        await strokeEvents.AppendEventAsync("demo-board", EventType.Add, NewStroke("user-2"), default);
        await strokeEvents.AppendEventAsync("demo-board", EventType.Add, NewStroke("user-2"), default);

        await hub.JoinBoard("demo-board", DateTime.UnixEpoch);

        Assert.HasCount(2, caller.Strokes);
    }

    [TestMethod]
    public async Task SendStroke_logs_once_and_broadcasts_once()
    {
        var hub = CreateHub(out _, out var group, out var boardRepository, out _, out _, out _, out var strokeEvents);
        await boardRepository.CreateBoardAsync("demo-board", default);

        await hub.JoinBoard("demo-board", DateTime.UnixEpoch);

        var stroke = new StrokeInput(
            Guid.NewGuid().ToString("D"),
            [new PointInput(1, 2, null, 0)],
            "#123456",
            3);

        await hub.SendStroke("demo-board", stroke);
        await hub.SendStroke("demo-board", stroke);

        Assert.AreEqual(1, strokeEvents.Events.Count(e => e.Type == EventType.Add));
        Assert.HasCount(1, group.Strokes);
        Assert.AreEqual(stroke.Id, group.Strokes[0].Id);
    }

    [TestMethod]
    public async Task UndoLastStroke_removes_callers_last_stroke_and_broadcasts()
    {
        var hub = CreateHub(out _, out var group, out var boardRepository, out _, out _, out _, out var strokeEvents);
        await boardRepository.CreateBoardAsync("demo-board", default);
        await hub.JoinBoard("demo-board", DateTime.UnixEpoch);

        var stroke = new StrokeInput(
            Guid.NewGuid().ToString("D"),
            [new PointInput(1, 2, null, 0)],
            "#123456",
            3);
        await hub.SendStroke("demo-board", stroke);

        await hub.UndoLastStroke("demo-board");

        Assert.AreEqual(1, strokeEvents.Events.Count(e => e.Type == EventType.Remove));
        Assert.HasCount(1, group.StrokeRemovedCalls);
        Assert.AreEqual(stroke.Id, group.StrokeRemovedCalls[0]);
    }

    [TestMethod]
    public async Task UndoLastStroke_is_noop_when_caller_has_no_strokes()
    {
        var hub = CreateHub(out _, out var group, out var boardRepository, out _, out _, out _, out var strokeEvents);
        await boardRepository.CreateBoardAsync("demo-board", default);
        await hub.JoinBoard("demo-board", DateTime.UnixEpoch);

        await hub.UndoLastStroke("demo-board");

        Assert.AreEqual(0, strokeEvents.Events.Count(e => e.Type == EventType.Remove));
        Assert.IsEmpty(group.StrokeRemovedCalls);
    }

    private static Stroke NewStroke(string userId)
    {
        return new Stroke
        {
            Id = Guid.NewGuid().ToString("D"),
            UserId = userId,
            Color = "#000000",
            Width = 2,
            Timestamp = DateTime.UtcNow,
            Points = [new Point { X = 0, Y = 0, Pressure = null, TimeOffset = 0 }]
        };
    }

    private static WhiteboardHub CreateHub(
        out TestWhiteboardClient caller,
        out TestWhiteboardClient group,
        out InMemoryBoardRepository boardRepository,
        out InMemoryUserProfileRepository userProfileRepository,
        out TestHubCallerContext context,
        out TestGroupManager groups,
        out InMemoryStrokeEventRepository strokeEventRepository)
    {
        caller = new TestWhiteboardClient();
        group = new TestWhiteboardClient();
        boardRepository = new InMemoryBoardRepository();
        userProfileRepository = new InMemoryUserProfileRepository();
        strokeEventRepository = new InMemoryStrokeEventRepository();
        context = new TestHubCallerContext("conn-" + Guid.NewGuid().ToString("N"), "user-1");
        groups = new TestGroupManager();

        var hub = new WhiteboardHub(boardRepository, userProfileRepository, strokeEventRepository, NullLogger<WhiteboardHub>.Instance)
        {
            Context = context,
            Clients = new TestHubCallerClients(caller, group),
            Groups = groups
        };
        return hub;
    }
}
