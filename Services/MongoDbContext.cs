using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

public interface IMongoDbContext
{
    Task<IMongoCollection<Board>> Boards { get; }

    Task<IMongoCollection<UserProfile>> Users { get; }

    /// <summary>
    /// The append-only stroke-event log. Backed by a native time-series collection
    /// that is created by <c>MongoDbContext.InitializeAsync</c>; accessing this
    /// before initialization completes throws.
    /// </summary>
    Task<IMongoCollection<StrokeEvent>> StrokeEvents { get; }
}

public sealed class MongoDbContext : IMongoDbContext, IHostedService
{
    private const string StrokeEventsCollectionName = "StrokeEvents";

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<Board> _boards;
    private readonly IMongoCollection<UserProfile> _users;
    private IMongoCollection<StrokeEvent>? _strokeEvents;
    private readonly Task _init;

    public MongoDbContext(IMongoClient client, IConfiguration configuration, IHostApplicationLifetime cancellationToken)
    {
        var databaseName = configuration["MongoDB:DatabaseName"]
            ?? throw new InvalidOperationException("MongoDB database name is not configured.");

        _database = client.GetDatabase(databaseName);
        _boards = _database.GetCollection<Board>("Boards");
        _users = _database.GetCollection<UserProfile>("Users");
        // Indexes should be created before access
        _init = Init(cancellationToken.ApplicationStopping);
    }

    public Task<IMongoCollection<Board>> Boards
    {
        get
        {
            return _init.ContinueWith((_) => _boards);
        }
    }

    public Task<IMongoCollection<UserProfile>> Users
    {
        get
        {
            return _init.ContinueWith((_) => _users);
        }
    }

    public Task<IMongoCollection<StrokeEvent>> StrokeEvents
    {
        get
        {
            return _init.ContinueWith((_) => _strokeEvents ?? throw new InvalidOperationException(
                "MongoDbContext.InitializeAsync must be awaited before accessing StrokeEvents."));
        }
    }

    /// <summary>
    /// Establishes server-side schema that cannot be created in the constructor —
    /// the native time-series <c>StrokeEvents</c> collection and its secondary
    /// indexes. Idempotent; must be awaited once during startup before the
    /// collections are used.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _init;
    }

    private async Task Init(CancellationToken cancellationToken) {
        var existing = await _database
            .ListCollectionNames(new ListCollectionNamesOptions
            {
                Filter = Builders<MongoDB.Bson.BsonDocument>.Filter.Eq("name", StrokeEventsCollectionName)
            })
            .ToListAsync(cancellationToken);

        if (existing.Count == 0)
        {
            // Native time-series collection: Timestamp is the timeField and BoardId
            // the metaField, so per-board history stores and queries efficiently.
            await _database.CreateCollectionAsync(
                StrokeEventsCollectionName,
                new CreateCollectionOptions
                {
                    TimeSeriesOptions = new TimeSeriesOptions("Timestamp", "BoardId", TimeSeriesGranularity.Seconds)
                },
                cancellationToken);
        }

        var collection = _database.GetCollection<StrokeEvent>(StrokeEventsCollectionName);

        // Time-series collections permit no unique index, so Add de-duplication is
        // application-level; these secondary indexes back the hot lookups.
        var strokeIdIndex = new CreateIndexModel<StrokeEvent>(
            Builders<StrokeEvent>.IndexKeys.Ascending(e => e.Stroke.Id),
            new CreateIndexOptions { Name = "ix_strokeevents_stroke_id" });

        var userTimestampIndex = new CreateIndexModel<StrokeEvent>(
            Builders<StrokeEvent>.IndexKeys.Ascending(e => e.Stroke.UserId).Ascending(e => e.Timestamp),
            new CreateIndexOptions { Name = "ix_strokeevents_user_timestamp" });

        await collection.Indexes.CreateManyAsync([strokeIdIndex, userTimestampIndex], cancellationToken);
        _strokeEvents = collection;
    }


    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
