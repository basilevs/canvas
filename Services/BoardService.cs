using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

public interface IBoardService
{
    Task<Board> CreateBoardAsync(string boardId, CancellationToken cancellationToken);

    Task<Board?> GetBoardAsync(string boardId, CancellationToken cancellationToken);

    Task<Board> GetOrCreateBoardAsync(string boardId, CancellationToken cancellationToken);

    Task UpdateLastActivityAsync(string boardId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Stroke>> GetSnapshotAsync(string boardId, CancellationToken cancellationToken);

    Task<bool> AddStrokeToSnapshotAsync(string boardId, Stroke stroke, CancellationToken cancellationToken);

    Task EnsureIndexesAsync(CancellationToken cancellationToken);
}

public sealed class BoardService : IBoardService
{
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(30);
    private readonly IMongoCollection<Board> _boards;

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

        await _boards.InsertOneAsync(board, cancellationToken: cancellationToken);
        return board;
    }

    public async Task<Board?> GetBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        return await _boards.Find(board => board.Id == boardId).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Board> GetOrCreateBoardAsync(string boardId, CancellationToken cancellationToken)
    {
        EnsureBoardId(boardId);

        var now = DateTime.UtcNow;
        var filter = Builders<Board>.Filter.Eq(board => board.Id, boardId);
        var update = Builders<Board>.Update
            .SetOnInsert(board => board.CreatedAt, now)
            .SetOnInsert(board => board.LastActivityAt, now)
            .SetOnInsert(board => board.ActiveStrokes, []);

        var options = new FindOneAndUpdateOptions<Board>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        return await _boards.FindOneAndUpdateAsync(filter, update, options, cancellationToken);
    }

    public async Task UpdateLastActivityAsync(string boardId, CancellationToken cancellationToken)
    {
        var update = Builders<Board>.Update.Set(board => board.LastActivityAt, DateTime.UtcNow);
        var result = await _boards.UpdateOneAsync(
            board => board.Id == boardId,
            update,
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            throw new KeyNotFoundException($"Board '{boardId}' was not found.");
        }
    }

    public async Task<IReadOnlyList<Stroke>> GetSnapshotAsync(string boardId, CancellationToken cancellationToken)
    {
        var board = await GetBoardAsync(boardId, cancellationToken);
        if (board is null)
        {
            return [];
        }

        return board.ActiveStrokes;
    }

    public async Task<bool> AddStrokeToSnapshotAsync(
        string boardId,
        Stroke stroke,
        CancellationToken cancellationToken)
    {
        if (stroke is null)
        {
            throw new ArgumentNullException(nameof(stroke));
        }

        if (string.IsNullOrWhiteSpace(stroke.Id))
        {
            throw new ArgumentException("Stroke id is required.", nameof(stroke));
        }

        var filter = Builders<Board>.Filter.And(
            Builders<Board>.Filter.Eq(board => board.Id, boardId),
            Builders<Board>.Filter.Not(
                Builders<Board>.Filter.ElemMatch(
                    board => board.ActiveStrokes,
                    existingStroke => existingStroke.Id == stroke.Id)));

        var update = Builders<Board>.Update.Push(board => board.ActiveStrokes, stroke);
        var result = await _boards.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);

        return result.ModifiedCount > 0;
    }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
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

        await _boards.Indexes.CreateOneAsync(ttlIndex, cancellationToken: cancellationToken);
    }

    private static void EnsureBoardId(string boardId)
    {
        if (string.IsNullOrWhiteSpace(boardId))
        {
            throw new ArgumentException("Board id is required.", nameof(boardId));
        }
    }
}
