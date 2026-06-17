using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

public interface IMongoDbContext
{
    IMongoDatabase Database { get; }

    IMongoCollection<Board> Boards { get; }

    IMongoCollection<UserProfile> Users { get; }

    IMongoCollection<StrokeEvent> StrokeEvents { get; }
}

public sealed class MongoDbContext : IMongoDbContext
{
    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<Board> _boards;
    private readonly IMongoCollection<UserProfile> _users;
    private readonly IMongoCollection<StrokeEvent> _strokeEvents;

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
        _strokeEvents = _database.GetCollection<StrokeEvent>("StrokeEvents");
    }

    public IMongoDatabase Database => _database;

    public IMongoCollection<Board> Boards => _boards;

    public IMongoCollection<UserProfile> Users => _users;

    public IMongoCollection<StrokeEvent> StrokeEvents => _strokeEvents;
}
