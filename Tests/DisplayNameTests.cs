using Canvas.Hubs;
using Canvas.Services;
using Microsoft.AspNetCore.SignalR;

namespace Canvas.Tests;

[TestClass]
public sealed class DisplayNameTests
{
    [TestMethod]
    public async Task SetDisplayName_persists_and_broadcasts()
    {
        var hub = CreateHub(out _, out var group, out var boardService, out var userProfileService, out _, out _);
        await boardService.CreateBoardAsync("demo-board", default);

        await hub.JoinBoard("demo-board");
        await hub.SetDisplayName("New Name");

        var profile = await userProfileService.GetOrCreateProfileAsync("user-1", default);
        Assert.AreEqual("New Name", profile.DisplayName);
        Assert.AreEqual(1, group.UserRenamedCalls.Count);
        Assert.AreEqual(("user-1", "New Name"), group.UserRenamedCalls[0]);
    }

    [TestMethod]
    public async Task SetDisplayName_rejects_empty_or_long_names()
    {
        var hub = CreateHub(out _, out _, out var boardService, out _, out _, out _);
        await boardService.CreateBoardAsync("demo-board", default);
        await hub.JoinBoard("demo-board");

        await AssertHubExceptionAsync(() => hub.SetDisplayName(""));
        await AssertHubExceptionAsync(() => hub.SetDisplayName(new string('x', 31)));
    }

    private static TestWhiteboardHub CreateHub(
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

        var hub = new TestWhiteboardHub(boardService, userProfileService);
        hub.Initialize(context, new TestHubCallerClients(caller, group), groups);
        return hub;
    }

    private static async Task AssertHubExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            Assert.Fail("Expected a HubException.");
        }
        catch (HubException)
        {
        }
    }
}
