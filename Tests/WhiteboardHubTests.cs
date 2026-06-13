using Canvas.Dtos;
using Canvas.Hubs;
using Canvas.Models;
using Canvas.Services;
using Microsoft.AspNetCore.SignalR;

namespace Canvas.Tests;

[TestClass]
public sealed class WhiteboardHubTests
{
    [TestMethod]
    public async Task JoinBoard_returns_snapshot_and_broadcasts_user_joined()
    {
        var hub = CreateHub(out var caller, out var group, out var boardService, out var userProfileService, out var context, out var groups);
        await boardService.CreateBoardAsync("demo-board", default);
        await userProfileService.SetDisplayNameAsync("user-1", "Alice", default);

        await hub.JoinBoard("Demo-Board");

        Assert.AreEqual(1, caller.Snapshots.Count);
        Assert.AreEqual("demo-board", caller.Snapshots[0].BoardName);
        Assert.AreEqual(0, caller.Snapshots[0].ActiveStrokes.Count);
        Assert.AreEqual(1, group.UserJoinedCalls.Count);
        Assert.AreEqual(("user-1", "Alice"), group.UserJoinedCalls[0]);
        Assert.IsTrue(groups.Operations.Any(operation => operation.Action == "add" && operation.GroupName == "demo-board"));
    }

    [TestMethod]
    public async Task SendStroke_stores_once_and_broadcasts_once()
    {
        var hub = CreateHub(out _, out var group, out var boardService, out _, out _, out _);
        await boardService.CreateBoardAsync("demo-board", default);

        await hub.JoinBoard("demo-board");

        var stroke = new StrokeInput(
            Guid.NewGuid().ToString("D"),
            [new PointInput(1, 2, null, 0)],
            "#123456",
            3,
            10);

        await hub.SendStroke("demo-board", stroke);
        await hub.SendStroke("demo-board", stroke);

        var board = await boardService.GetBoardAsync("demo-board", default);
        Assert.IsNotNull(board);
        Assert.AreEqual(1, board.ActiveStrokes.Count);
        Assert.AreEqual(1, group.Strokes.Count);
        Assert.AreEqual(stroke.Id, group.Strokes[0].Id);
    }

    private static WhiteboardHub CreateHub(
        out TestWhiteboardClient caller,
        out TestWhiteboardClient group,
        out InMemoryBoardService boardService,
        out InMemoryUserProfileService userProfileService,
        out TestHubCallerContext context,
        out TestGroupManager groups)
    {
        caller = new TestWhiteboardClient();
        group = new TestWhiteboardClient();
        boardService = new InMemoryBoardService();
        userProfileService = new InMemoryUserProfileService();
        context = new TestHubCallerContext("conn-" + Guid.NewGuid().ToString("N"), "user-1");
        groups = new TestGroupManager();

        var hub = new WhiteboardHub(boardService, userProfileService)
        {
            Context = context,
            Clients = new TestHubCallerClients(caller, group),
            Groups = groups
        };
        return hub;
    }
}
