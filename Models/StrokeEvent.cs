using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Canvas.Models;

/// <summary>
/// The kind of change an append-only <see cref="StrokeEvent"/> records.
/// </summary>
public enum EventType
{
    /// <summary>A stroke was drawn and added to the board.</summary>
    Add,

    /// <summary>A previously added stroke was removed (undo).</summary>
    Remove
}

/// <summary>
/// An append-only event in a board's stroke history. Persisted in the
/// <c>StrokeEvents</c> native MongoDB time-series collection keyed on
/// <see cref="Timestamp"/> (the <c>timeField</c>) and <see cref="BoardId"/>
/// (the <c>metaField</c>). The event log is the single source of truth for
/// board state; there is no per-board sequence number — events are ordered by
/// <see cref="Timestamp"/>, with the embedded stroke's stable <c>Id</c> as the
/// tiebreaker.
/// </summary>
public sealed class StrokeEvent
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string BoardId { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public EventType Type { get; set; }

    public Stroke Stroke { get; set; } = new();

    public DateTime Timestamp { get; set; }
}
