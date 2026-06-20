using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

public interface IBoardService
{
    Task<Board> CreateBoardAsync(string boardId, CancellationToken cancellationToken);

    Task<Board?> GetBoardAsync(string boardId, CancellationToken cancellationToken);

    Task<Board> GetOrCreateBoardAsync(string boardId, CancellationToken cancellationToken);

    Task UpdateLastActivityAsync(string boardId, CancellationToken cancellationToken);
}

public sealed class BoardService : IBoardService, IHostedService
{
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(30);
    private readonly Task<IMongoCollection<Board>> _boards;

    public BoardService(IMongoDbContext mongoDbContext)
    {
        _boards = mongoDbContext.Boards;
    }

    public async Task<Board> CreateBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        EnsureBoardId(boardId);

        var now = DateTime.UtcNow;
        var board = new Board
        {
            Id = boardId,
            CreatedAt = now,
            LastActivityAt = now
        };

        var boards = await _boards;
        await boards.InsertOneAsync(board, cancellationToken: cancellationToken);
        return board;
    }

    public async Task<Board?> GetBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        var boards = await _boards;
        return await boards.Find(board => board.Id == boardId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Board> GetOrCreateBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        EnsureBoardId(boardId);

        var now = DateTime.UtcNow;
        var filter = Builders<Board>.Filter.Eq(board => board.Id, boardId);
        var update = Builders<Board>.Update
            .SetOnInsert(board => board.CreatedAt, now)
            .SetOnInsert(board => board.LastActivityAt, now);

        var options = new FindOneAndUpdateOptions<Board>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        var boards = await _boards;
        return await boards.FindOneAndUpdateAsync(filter, update, options, cancellationToken);
    }

    public async Task UpdateLastActivityAsync(string boardId, CancellationToken cancellationToken)
    {
        var boards = await _boards;
        var update = Builders<Board>.Update.Set(board => board.LastActivityAt, DateTime.UtcNow);
        var result = await boards.UpdateOneAsync(
            board => board.Id == boardId,
            update,
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            throw new KeyNotFoundException($"Board '{boardId}' was not found.");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The canonical board name is the document _id, so it is inherently unique —
        // no separate unique Name index is required. Only the activity TTL is created.
        var ttlIndex = new CreateIndexModel<Board>(
            Builders<Board>.IndexKeys.Ascending(board => board.LastActivityAt),
            new CreateIndexOptions
            {
                Name = "ttl_boards_last_activity",
                ExpireAfter = SnapshotRetention
            });

        var boards = await _boards;
        await boards.Indexes.CreateOneAsync(ttlIndex, cancellationToken: cancellationToken);
    }

    private static void EnsureBoardId(string boardId)
    {
        if (string.IsNullOrWhiteSpace(boardId))
        {
            throw new ArgumentException("Board id is required.", nameof(boardId));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
