using Canvas.Services;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace Canvas.Tests;

/// <summary>
/// Shared helper for connectivity-gated MongoDB integration tests. Resolves the
/// cluster connection string from user-secrets (the same id the app uses) or the
/// <c>MongoDB__ConnectionString</c> environment variable, and provisions a uniquely
/// named throwaway database that the caller drops on cleanup. When the cluster is
/// unreachable, <see cref="CreateContextAsync"/> calls <c>Assert.Inconclusive</c>
/// so the suite stays green in offline environments.
/// </summary>
internal static class MongoTestSupport
{
    public static string? ResolveConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            // Reads the UserSecretsId attribute the SDK emits on the canvas assembly
            // from its <UserSecretsId> MSBuild property — no duplicated id to drift.
            .AddUserSecrets(typeof(Program).Assembly)
            .AddEnvironmentVariables()
            .Build();

        return configuration["MongoDB:ConnectionString"];
    }

    /// <summary>Generates a unique test database name within Mongo's 38-byte limit.</summary>
    public static string NewDatabaseName()
    {
        return "canvas_test_" + Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>
    /// Creates a <see cref="MongoDbContext"/> bound to a fresh test database and
    /// verifies connectivity with a ping. Returns the context, the owning client,
    /// and the database name; the test must drop the database via
    /// <see cref="DropDatabaseAsync"/>.
    /// </summary>
    public static async Task<(MongoDbContext Context, IMongoClient Client, string DatabaseName)> CreateContextAsync(CancellationToken cancellationToken)
    {
        var connectionString = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("No MongoDB connection string configured (user-secrets MongoDB:ConnectionString or MongoDB__ConnectionString).");
        }

        var databaseName = NewDatabaseName();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDB:DatabaseName"] = databaseName
            })
            .Build();

        var client = new MongoClient(connectionString);

        try
        {
            await client.GetDatabase(databaseName).RunCommandAsync(
                (Command<MongoDB.Bson.BsonDocument>)"{ ping: 1 }",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Assert.Inconclusive($"MongoDB cluster is unreachable: {ex.Message}");
        }

        var context = new MongoDbContext(client, configuration);
        return (context, client, databaseName);
    }

    public static Task DropDatabaseAsync(IMongoClient client, string databaseName)
    {
        return client.DropDatabaseAsync(databaseName);
    }
}
