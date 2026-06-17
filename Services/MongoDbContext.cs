using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

public interface IMongoDbContext
{
    IMongoDatabase Database { get; }

    IMongoCollection<Board> Boards { get; }

    IMongoCollection<UserProfile> Users { get; }

    /// <summary>
    /// The append-only stroke-event log. Backed by a native time-series collection
    /// that is created by <see cref="InitializeAsync"/>; accessing this before
    /// initialization completes throws.
    /// </summary>
    IMongoCollection<StrokeEvent> StrokeEvents { get; }

    /// <summary>
    /// Establishes server-side schema that cannot be created in the constructor —
    /// the native time-series <c>StrokeEvents</c> collection and its secondary
    /// indexes. Idempotent; must be awaited once during startup before the
    /// collections are used.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken);
}

public sealed class MongoDbContext : IMongoDbContext
{
    private const string StrokeEventsCollectionName = "StrokeEvents";

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<Board> _boards;
    private readonly IMongoCollection<UserProfile> _users;
    private IMongoCollection<StrokeEvent>? _strokeEvents;

    public MongoDbContext(IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"]
            ?? throw new InvalidOperationException("MongoDB connection string is not configured.");
        var databaseName = configuration["MongoDB:DatabaseName"]
            ?? throw new InvalidOperationException("MongoDB database name is not configured.");

        var client = new MongoClient(connectionString);
        _database = client.GetDatabase(databaseName);
        _boards = _database.GetCollection<Board>("Boards");
        _users = _database.GetCollection<UserProfile>("Users");
    }

    public IMongoDatabase Database => _database;

    public IMongoCollection<Board> Boards => _boards;

    public IMongoCollection<UserProfile> Users => _users;

    public IMongoCollection<StrokeEvent> StrokeEvents =>
        _strokeEvents ?? throw new InvalidOperationException(
            "MongoDbContext.InitializeAsync must be awaited before accessing StrokeEvents.");

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
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
}
