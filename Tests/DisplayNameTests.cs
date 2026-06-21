using Canvas.Hubs;
using Canvas.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Canvas.Tests;

[TestClass]
public sealed class DisplayNameTests
{
    [TestMethod]
    public async Task SetDisplayName_persists_and_broadcasts()
    {
        var hub = CreateHub(out _, out var group, out var boardRepository, out var userProfileRepository, out _, out _);
        await boardRepository.CreateBoardAsync("demo-board", default);

        await hub.JoinBoard("demo-board", DateTime.UnixEpoch);
        await hub.SetDisplayName("New Name");

        var profile = await userProfileRepository.GetOrCreateProfileAsync("user-1", default);
        Assert.AreEqual("New Name", profile.DisplayName);
        Assert.HasCount(1, group.UserRenamedCalls);
        Assert.AreEqual(("user-1", "New Name"), group.UserRenamedCalls[0]);
    }

    [TestMethod]
    public async Task SetDisplayName_rejects_empty_or_long_names()
    {
        var hub = CreateHub(out _, out _, out var boardRepository, out _, out _, out _);
        await boardRepository.CreateBoardAsync("demo-board", default);
        await hub.JoinBoard("demo-board", DateTime.UnixEpoch);

        await AssertHubExceptionAsync(() => hub.SetDisplayName(""));
        await AssertHubExceptionAsync(() => hub.SetDisplayName(new string('x', 31)));
    }

    private static WhiteboardHub CreateHub(
        out TestWhiteboardClient caller,
        out TestWhiteboardClient group,
        out InMemoryBoardRepository boardRepository,
        out InMemoryUserProfileRepository userProfileRepository,
        out TestHubCallerContext context,
        out TestGroupManager groups)
    {
        caller = new TestWhiteboardClient();
        group = new TestWhiteboardClient();
        boardRepository = new InMemoryBoardRepository();
        userProfileRepository = new InMemoryUserProfileRepository();
        context = new TestHubCallerContext("conn-" + Guid.NewGuid().ToString("N"), "user-1");
        groups = new TestGroupManager();

        var hub = new WhiteboardHub(boardRepository, userProfileRepository, new InMemoryStrokeEventRepository(), NullLogger<WhiteboardHub>.Instance)
        {
            Context = context,
            Clients = new TestHubCallerClients(caller, group),
            Groups = groups
        };
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
