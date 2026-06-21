using Canvas.Models;
using Canvas.Services;
using MongoDB.Driver;

namespace Canvas.Tests;

[TestClass]
public sealed class StrokeEventRepositoryTests
{
    private MongoDbContext _context = null!;
    private IMongoClient _client = null!;
    private string _databaseName = null!;
    private StrokeEventRepository _service = null!;

    [TestInitialize]
    public async Task SetUpAsync()
    {
        (_context, _client, _databaseName) = await MongoTestSupport.CreateContextAsync(TestContext.CancellationTokenSource.Token);
        _service = new StrokeEventRepository(_context);
    }

    [TestCleanup]
    public async Task TearDownAsync()
    {
        if (_client is not null && _databaseName is not null)
        {
            await MongoTestSupport.DropDatabaseAsync(_client, _databaseName);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AppendEventAsync_stamps_server_timestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        await _service.AppendEventAsync("board-1", EventType.Add, NewStroke("user-1"), default);

        var page = await _service.GetEventsPageAsync("board-1", 1, StrokeEventRepository.DefaultPageSize, default);
        Assert.HasCount(1, page.Events);
        Assert.IsTrue(page.Events[0].Timestamp >= before);
        Assert.IsTrue(page.Events[0].Timestamp <= DateTime.UtcNow.AddSeconds(1));
    }

    [TestMethod]
    public async Task GetEventsPageAsync_returns_oldest_first_with_totals()
    {
        var first = NewStroke("user-1");
        var second = NewStroke("user-1");
        var third = NewStroke("user-1");
        await _service.AppendEventAsync("board-1", EventType.Add, first, default);
        await Task.Delay(10);
        await _service.AppendEventAsync("board-1", EventType.Add, second, default);
        await Task.Delay(10);
        await _service.AppendEventAsync("board-1", EventType.Add, third, default);

        var page = await _service.GetEventsPageAsync("board-1", 1, StrokeEventRepository.DefaultPageSize, default);

        Assert.AreEqual(3, page.TotalEvents);
        Assert.AreEqual(1, page.TotalPages);
        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id, third.Id },
            page.Events.Select(e => e.Stroke.Id).ToArray());
    }

    [TestMethod]
    public async Task AppendEventAsync_does_not_double_log_resent_add()
    {
        var stroke = NewStroke("user-1");

        var firstAppend = await _service.AppendEventAsync("board-1", EventType.Add, stroke, default);
        var secondAppend = await _service.AppendEventAsync("board-1", EventType.Add, stroke, default);

        Assert.IsTrue(firstAppend);
        Assert.IsFalse(secondAppend);

        var page = await _service.GetEventsPageAsync("board-1", 1, StrokeEventRepository.DefaultPageSize, default);
        Assert.AreEqual(1, page.TotalEvents);
    }

    [TestMethod]
    public async Task GetLastRemovableStrokeByUserAsync_returns_most_recent_non_removed()
    {
        var first = NewStroke("user-1");
        var second = NewStroke("user-1");
        await _service.AppendEventAsync("board-1", EventType.Add, first, default);
        await Task.Delay(10);
        await _service.AppendEventAsync("board-1", EventType.Add, second, default);

        var removable = await _service.GetLastRemovableStrokeByUserAsync("board-1", "user-1", default);
        Assert.IsNotNull(removable);
        Assert.AreEqual(second.Id, removable.Id);

        await _service.AppendEventAsync("board-1", EventType.Remove, second, default);
        var afterRemoval = await _service.GetLastRemovableStrokeByUserAsync("board-1", "user-1", default);
        Assert.IsNotNull(afterRemoval);
        Assert.AreEqual(first.Id, afterRemoval.Id);

        await _service.AppendEventAsync("board-1", EventType.Remove, first, default);
        var noneLeft = await _service.GetLastRemovableStrokeByUserAsync("board-1", "user-1", default);
        Assert.IsNull(noneLeft);
    }

    [TestMethod]
    public async Task GetEventsSinceAsync_is_inclusive_of_boundary()
    {
        var first = NewStroke("user-1");
        await _service.AppendEventAsync("board-1", EventType.Add, first, default);
        var page = await _service.GetEventsPageAsync("board-1", 1, StrokeEventRepository.DefaultPageSize, default);
        var boundary = page.Events[0].Timestamp;

        var since = await _service.GetEventsSinceAsync("board-1", boundary, default);

        Assert.HasCount(1, since);
        Assert.AreEqual(first.Id, since[0].Stroke.Id);
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
            Points = [new Point { X = 1, Y = 2, Pressure = null, TimeOffset = 0 }]
        };
    }
}
