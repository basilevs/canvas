using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace Canvas.Tests;

/// <summary>
/// Verifies static assets and the SPA fallback are served with a revalidating
/// <c>Cache-Control</c> so a redeploy is never masked by the browser's heuristic
/// caching of the unversioned asset URLs.
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
    public async Task Static_asset_is_served_with_no_cache()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/js/app.js");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(response.Headers.CacheControl);
        Assert.IsTrue(response.Headers.CacheControl!.NoCache, "Static assets must be served with Cache-Control: no-cache.");
    }

    [TestMethod]
    public async Task Spa_fallback_index_is_served_with_no_cache()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/boards/example");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(response.Headers.CacheControl);
        Assert.IsTrue(response.Headers.CacheControl!.NoCache, "The SPA fallback must be served with Cache-Control: no-cache.");
    }
}
