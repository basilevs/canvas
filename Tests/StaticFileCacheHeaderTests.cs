using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Canvas.Tests;

/// <summary>
/// Verifies static assets and the SPA fallback are served with a stale-while-
/// revalidate <c>Cache-Control</c> so the browser shows the cached (old) copy
/// instantly and revalidates in the background, picking up a redeploy on the next
/// load rather than masking it with heuristic caching.
/// </summary>
[TestClass]
public sealed class StaticFileCacheHeaderTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _databaseName = null!;

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
            // Force host startup so connectivity failures surface as Inconclusive.
            _ = _factory.Services.GetRequiredService<IMongoClient>();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
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
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                // Best-effort cleanup.
            }

            _factory.Dispose();
        }
    }

    [TestMethod]
    public async Task Static_asset_is_served_with_stale_while_revalidate()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/js/app.js");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        AssertStaleWhileRevalidate(response.Headers.CacheControl, "Static assets");
    }

    [TestMethod]
    public async Task Spa_fallback_index_is_served_with_stale_while_revalidate()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/boards/example");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        AssertStaleWhileRevalidate(response.Headers.CacheControl, "The SPA fallback");
    }

    private static void AssertStaleWhileRevalidate(
        System.Net.Http.Headers.CacheControlHeaderValue? cacheControl,
        string subject)
    {
        Assert.IsNotNull(cacheControl, $"{subject} must carry a Cache-Control header.");
        Assert.AreEqual(
            TimeSpan.Zero,
            cacheControl!.MaxAge,
            $"{subject} must be served with max-age=0 so the cached copy is always revalidated.");
        Assert.IsTrue(
            cacheControl.Extensions.Any(extension =>
                string.Equals(extension.Name, "stale-while-revalidate", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(extension.Value)),
            $"{subject} must be served with stale-while-revalidate so the old copy is shown while revalidating.");
    }
}
