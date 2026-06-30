using Canvas.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Canvas.Tests;

/// <summary>
/// Shared helper for connectivity-gated MongoDB integration tests. Resolves the
/// cluster connection string from user-secrets (the same id the app uses) or the
/// <c>MongoDB__ConnectionString</c> environment variable, and provisions a uniquely
/// named throwaway database that the caller drops on cleanup. When the cluster is
/// unreachable, <see cref="MongoTestSupport"/> constructor calls <c>Assert.Inconclusive</c>
/// so the suite stays green in offline environments.
/// </summary>
internal class MongoTestSupport: IAsyncDisposable
{
    public readonly MongoDbContext Context;
    public readonly IMongoClient Client;
    private readonly string _databaseName;

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
    /// verifies connectivity with a ping.
    /// The test must drop the database via
    /// <see cref="DisposeAsync"/>.
    /// </summary>
    public MongoTestSupport(CancellationToken cancellationToken)
    {
        var connectionString = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("No MongoDB connection string configured (user-secrets MongoDB:ConnectionString or MongoDB__ConnectionString).");
        }

        _databaseName = NewDatabaseName();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDB:DatabaseName"] = _databaseName
            })
            .Build();

        Client = new MongoClient(connectionString);

        try
        {
            Client.GetDatabase(_databaseName).RunCommand(
                (Command<MongoDB.Bson.BsonDocument>)"{ ping: 1 }",
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Assert.Inconclusive($"MongoDB cluster is unreachable: {ex.Message}");
        }


        var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<MongoDbContext>();
        Context = new MongoDbContext(Client, configuration, ICancellationTokenProvider.Wrap(cancellationToken), logger);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DropDatabaseAsync(_databaseName);
    }
}
