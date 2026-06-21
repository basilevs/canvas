using Canvas.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Canvas.Services;

/// <summary>
/// Lightweight metadata for a single history page: the pagination totals plus the
/// timestamp of the page's most-recent event. Lets a caller compute cache
/// validators without transferring the page's documents.
/// </summary>
public sealed record StrokeEventPageInfo(long TotalEvents, int TotalPages, DateTime LastEventTimestamp);

public interface IStrokeEventRepository
{
    /// <summary>
    /// Appends an event to the board's log, stamping the server timestamp. For
    /// <see cref="EventType.Add"/> events this is the insert-time de-duplication
    /// authority: an <c>Add</c> whose stroke <c>Id</c> already exists on the board
    /// is skipped. Returns <see langword="true"/> when a document was inserted.
    /// </summary>
    Task<bool> AppendEventAsync(string boardId, EventType type, Stroke stroke, CancellationToken cancellationToken);

    /// <summary>
    /// Returns pagination totals and the timestamp of the page's most-recent event
    /// in a single round-trip without transferring the page's documents, or
    /// <see langword="null"/> when the requested page is beyond the last page.
    /// Use to answer conditional GETs without paying the bandwidth of the page.
    /// </summary>
    Task<StrokeEventPageInfo?> GetEventsPageInfoAsync(string boardId, int pageNumber, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Returns just the oldest-first events of a page, without re-counting the log.
    /// Pair with <see cref="GetEventsPageInfoAsync"/>, which already supplies the
    /// totals, to materialize a page body in a single additional round-trip.
    /// </summary>
    Task<IReadOnlyList<StrokeEvent>> GetPageEventsAsync(string boardId, int pageNumber, int pageSize, CancellationToken cancellationToken);

    /// <summary>Returns all events with <c>Timestamp &gt;= sinceTimestamp</c>, oldest-first (inclusive).</summary>
    Task<IReadOnlyList<StrokeEvent>> GetEventsSinceAsync(string boardId, DateTime sinceTimestamp, CancellationToken cancellationToken);

    /// <summary>
    /// Bounded, index-backed lookup of the caller's most recently added stroke that
    /// has not since been removed, or <see langword="null"/> when none remain.
    /// </summary>
    Task<Stroke?> GetLastRemovableStrokeByUserAsync(string boardId, string userId, CancellationToken cancellationToken);
}

public sealed class StrokeEventRepository : IStrokeEventRepository
{
    /// <summary>Default page size for history pagination, optimized for throughput.</summary>
    public const int DefaultPageSize = 5000;

    // Bounded scan window for the last-removable-stroke lookup so undo never folds
    // the whole log (GUD-001): a small number of recent Adds are inspected newest-first.
    private const int LastRemovableScanLimit = 200;

    private readonly IMongoDbContext _context;

    public StrokeEventRepository(IMongoDbContext mongoDbContext)
    {
        _context = mongoDbContext;
    }

    private Task<IMongoCollection<StrokeEvent>> Events => _context.StrokeEventsAsync;

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

    public async Task<StrokeEventPageInfo?> GetEventsPageInfoAsync(string boardId, int pageNumber, int pageSize, CancellationToken cancellationToken)
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

        // One $facet aggregation yields both the board's total event count and the
        // timestamp of the requested page's most-recent event in a single
        // round-trip, transferring only those two scalars rather than the page's
        // documents. `pageLast` walks the same chronological order as the page read
        // and groups out the final event's timestamp via $last so the boundary
        // matches GetPageEventsAsync exactly.
        var pipeline = new BsonDocument[]
        {
            new("$match", new BsonDocument("BoardId", boardId)),
            new("$facet", new BsonDocument
            {
                ["total"] = new BsonArray { new BsonDocument("$count", "value") },
                ["pageLast"] = new BsonArray
                {
                    new BsonDocument("$sort", new BsonDocument { ["Timestamp"] = 1, ["Stroke.Id"] = 1 }),
                    new BsonDocument("$skip", (long)(pageNumber - 1) * pageSize),
                    new BsonDocument("$limit", (long)pageSize),
                    new BsonDocument("$group", new BsonDocument
                    {
                        ["_id"] = BsonNull.Value,
                        ["value"] = new BsonDocument("$last", "$Timestamp")
                    })
                }
            })
        };

        var events = await Events;
        var facet = await events
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .FirstAsync(cancellationToken);

        // $count emits no document for an empty match, so an absent total means zero.
        var totalArray = facet["total"].AsBsonArray;
        var totalEvents = totalArray.Count == 0 ? 0L : totalArray[0]["value"].ToInt64();
        var totalPages = (int)((totalEvents + pageSize - 1) / pageSize);

        if (pageNumber > totalPages)
        {
            return null;
        }

        // pageNumber <= totalPages guarantees the page holds at least one event, so
        // the grouped boundary timestamp is present.
        var lastTimestamp = facet["pageLast"].AsBsonArray[0]["value"].ToUniversalTime();
        return new StrokeEventPageInfo(totalEvents, totalPages, lastTimestamp);
    }

    public async Task<IReadOnlyList<StrokeEvent>> GetPageEventsAsync(string boardId, int pageNumber, int pageSize, CancellationToken cancellationToken)
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
        return await (await Events).Find(filter)
            .Sort(ChronologicalSort)
            .Skip((pageNumber - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);
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
