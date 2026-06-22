using System.Net;
using System.Net.Http.Json;
using Canvas.Dtos;
using Canvas.Models;
using Canvas.Services;
using Microsoft.AspNetCore.Mvc.Testing;using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Canvas.Tests;

[TestClass]
public sealed class ReplayHistoryEndpointTests
{
    private const int PageSize = StrokeEventRepository.DefaultPageSize;

    private WebApplicationFactory<Program> _factory = null!;
    private string _databaseName = null!;
    private IStrokeEventRepository _strokeEvents = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void SetUp()
    {
        var connectionString = MongoTestSupport.ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("No MongoDB connection string configured.");
        }

        _databaseName = MongoTestSupport.NewDatabaseName();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MongoDB:ConnectionString"] = connectionString,
                    ["MongoDB:DatabaseName"] = _databaseName
                });
            });
        });

        try
        {
            _strokeEvents = _factory.Services.GetRequiredService<IStrokeEventRepository>();
        }
        catch (Exception ex) when (ex is MongoDB.Driver.MongoException or TimeoutException)
        {
            Assert.Inconclusive($"MongoDB cluster is unreachable: {ex.Message}");
        }
    }

    [TestCleanup]
    public async Task TearDownAsync()
    {
        if (_factory is not null)
        {
            try
            {
                var client = _factory.Services.GetRequiredService<IMongoClient>();
                await client.DropDatabaseAsync(_databaseName);
            }
            catch (Exception ex) when (ex is MongoDB.Driver.MongoException or TimeoutException)
            {
                // Best-effort cleanup.
            }

            _factory.Dispose();
        }
    }

    [TestMethod]
    public async Task History_returns_pagination_metadata_oldest_first()
    {
        await SeedBoardAsync("alpha");
        var first = await AppendAsync("alpha", "user-1");
        await Task.Delay(10);
        var second = await AppendAsync("alpha", "user-1");

        using var client = _factory.CreateClient();
        var page = await client.GetFromJsonAsync<HistoryPageResponse>("/api/boards/alpha/history/1");

        Assert.IsNotNull(page);
        Assert.AreEqual(1, page.PageNumber);
        Assert.AreEqual(2, page.TotalEvents);
        Assert.AreEqual(1, page.TotalPages);
        CollectionAssert.AreEqual(
            new[] { first, second },
            page.Events.Select(e => e.Stroke.Id).ToArray());
    }

    [TestMethod]
    public async Task History_final_page_is_no_cache_with_last_modified()
    {
        await SeedBoardAsync("beta");
        await AppendAsync("beta", "user-1");

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/boards/beta/history/1");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString());
        Assert.IsNotNull(response.Content.Headers.LastModified);
    }

    [TestMethod]
    public async Task History_conditional_get_returns_304_for_unchanged_page()
    {
        await SeedBoardAsync("gamma");
        await AppendAsync("gamma", "user-1");

        using var client = _factory.CreateClient();
        using var initial = await client.GetAsync("/api/boards/gamma/history/1");
        var lastModified = initial.Content.Headers.LastModified;
        Assert.IsNotNull(lastModified);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/boards/gamma/history/1");
        request.Headers.IfModifiedSince = lastModified;
        using var conditional = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.NotModified, conditional.StatusCode);
    }

    [TestMethod]
    public async Task History_conditional_get_does_not_match_a_different_timestamp()
    {
        await SeedBoardAsync("delta");
        await AppendAsync("delta", "user-1");

        using var client = _factory.CreateClient();
        using var initial = await client.GetAsync("/api/boards/delta/history/1");
        var lastModified = initial.Content.Headers.LastModified;
        Assert.IsNotNull(lastModified);

        // A timestamp that is newer than Last-Modified must NOT yield 304: the
        // server compares the exact emitted Last-Modified string, it does not
        // treat any sufficiently-recent client date as "still fresh".
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/boards/delta/history/1");
        request.Headers.IfModifiedSince = lastModified.Value.AddSeconds(1);
        using var conditional = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, conditional.StatusCode);
    }

    [TestMethod]
    public async Task History_nonexistent_page_returns_404()
    {
        await SeedBoardAsync("delta");
        await AppendAsync("delta", "user-1");

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/boards/delta/history/2");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task History_empty_board_returns_404_for_page_one()
    {
        await SeedBoardAsync("epsilon");

        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/api/boards/epsilon/history/1");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task SeedBoardAsync(string boardId)
    {
        var boardRepository = _factory.Services.GetRequiredService<IBoardRepository>();
        await boardRepository.GetOrCreateBoardAsync(boardId, 1.0, TestContext.CancellationTokenSource.Token);
    }

    private async Task<string> AppendAsync(string boardId, string userId)
    {
        var stroke = new Stroke
        {
            Id = Guid.NewGuid().ToString("D"),
            UserId = userId,
            Color = "#000000",
            Width = 2,
            Timestamp = DateTime.UtcNow,
            Points = [new Point { X = 1, Y = 2, Pressure = null, TimeOffset = 0 }]
        };

        await _strokeEvents.AppendEventAsync(boardId, EventType.Add, stroke, TestContext.CancellationTokenSource.Token);
        return stroke.Id;
    }
}
