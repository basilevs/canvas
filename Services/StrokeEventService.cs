using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

/// <summary>A page of stroke-history events plus pagination totals.</summary>
public sealed record StrokeEventPage(IReadOnlyList<StrokeEvent> Events, long TotalEvents, int TotalPages);

public interface IStrokeEventService
{
    /// <summary>
    /// Appends an event to the board's log, stamping the server timestamp. For
    /// <see cref="EventType.Add"/> events this is the insert-time de-duplication
    /// authority: an <c>Add</c> whose stroke <c>Id</c> already exists on the board
    /// is skipped. Returns <see langword="true"/> when a document was inserted.
    /// </summary>
    Task<bool> AppendEventAsync(string boardId, EventType type, Stroke stroke, CancellationToken cancellationToken);

    /// <summary>Returns an oldest-first page of the board's events plus totals.</summary>
    Task<StrokeEventPage> GetEventsPageAsync(string boardId, int pageNumber, int pageSize, CancellationToken cancellationToken);

    /// <summary>Returns all events with <c>Timestamp &gt;= sinceTimestamp</c>, oldest-first (inclusive).</summary>
    Task<IReadOnlyList<StrokeEvent>> GetEventsSinceAsync(string boardId, DateTime sinceTimestamp, CancellationToken cancellationToken);

    /// <summary>
    /// Bounded, index-backed lookup of the caller's most recently added stroke that
    /// has not since been removed, or <see langword="null"/> when none remain.
    /// </summary>
    Task<Stroke?> GetLastRemovableStrokeByUserAsync(string boardId, string userId, CancellationToken cancellationToken);
}

public sealed class StrokeEventService : IStrokeEventService
{
    /// <summary>Default page size for history pagination, optimized for throughput.</summary>
    public const int DefaultPageSize = 5000;

    // Bounded scan window for the last-removable-stroke lookup so undo never folds
    // the whole log (GUD-001): a small number of recent Adds are inspected newest-first.
    private const int LastRemovableScanLimit = 200;

    private readonly IMongoDbContext _context;

    public StrokeEventService(IMongoDbContext mongoDbContext)
    {
        _context = mongoDbContext;
    }

    private Task<IMongoCollection<StrokeEvent>> Events => _context.StrokeEvents;

    public async Task<bool> AppendEventAsync(string boardId, EventType type, Stroke stroke, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boardId);
        ArgumentNullException.ThrowIfNull(stroke);
        if (string.IsNullOrWhiteSpace(stroke.Id))
        {
            throw new ArgumentException("Stroke id is required.", nameof(stroke));
        }

        var events = await Events;

        if (type == EventType.Add)
        {
            // Time-series collections support no unique index, so de-duplicate Add
            // events at the application level by the stroke's client-generated Id.
            var duplicate = await events
                .Find(e => e.BoardId == boardId && e.Type == EventType.Add && e.Stroke.Id == stroke.Id)
                .Limit(1)
                .AnyAsync(cancellationToken);
            if (duplicate)
            {
                return false;
            }
        }

        var strokeEvent = new StrokeEvent
        {
            Id = MongoDB.Bson.ObjectId.GenerateNewId(),
            BoardId = boardId,
            Type = type,
            Stroke = stroke,
            Timestamp = DateTime.UtcNow
        };

        await events.InsertOneAsync(strokeEvent, cancellationToken: cancellationToken);
        return true;
    }

    public async Task<StrokeEventPage> GetEventsPageAsync(string boardId, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boardId);
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var filter = Builders<StrokeEvent>.Filter.Eq(e => e.BoardId, boardId);
        var events = await Events;
        var totalEvents = await events.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var totalPages = (int)((totalEvents + pageSize - 1) / pageSize);

        if (pageNumber > totalPages)
        {
            return new StrokeEventPage([], totalEvents, totalPages);
        }

        var eventsList = await events.Find(filter)
            .Sort(ChronologicalSort)
            .Skip((pageNumber - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        return new StrokeEventPage(eventsList, totalEvents, totalPages);
    }

    public async Task<IReadOnlyList<StrokeEvent>> GetEventsSinceAsync(string boardId, DateTime sinceTimestamp, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boardId);

        var filter = Builders<StrokeEvent>.Filter.And(
            Builders<StrokeEvent>.Filter.Eq(e => e.BoardId, boardId),
            Builders<StrokeEvent>.Filter.Gte(e => e.Timestamp, sinceTimestamp));

        return await (await Events)
            .Find(filter)
            .Sort(ChronologicalSort)
            .ToListAsync(cancellationToken);
    }

    public async Task<Stroke?> GetLastRemovableStrokeByUserAsync(string boardId, string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boardId);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return null;
        }

        var addFilter = Builders<StrokeEvent>.Filter.And(
            Builders<StrokeEvent>.Filter.Eq(e => e.BoardId, boardId),
            Builders<StrokeEvent>.Filter.Eq(e => e.Type, EventType.Add),
            Builders<StrokeEvent>.Filter.Eq(e => e.Stroke.UserId, userId));

        var events = await Events;
        var recentAdds = await events
            .Find(addFilter)
            .Sort(Builders<StrokeEvent>.Sort.Descending(e => e.Timestamp).Descending(e => e.Stroke.Id))
            .Limit(LastRemovableScanLimit)
            .ToListAsync(cancellationToken);

        foreach (var add in recentAdds)
        {
            var removed = await events
                .Find(e => e.BoardId == boardId && e.Type == EventType.Remove && e.Stroke.Id == add.Stroke.Id)
                .Limit(1)
                .AnyAsync(cancellationToken);
            if (!removed)
            {
                return add.Stroke;
            }
        }

        return null;
    }

    private static SortDefinition<StrokeEvent> ChronologicalSort =>
        Builders<StrokeEvent>.Sort.Ascending(e => e.Timestamp).Ascending(e => e.Stroke.Id);
}
