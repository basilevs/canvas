using MongoDB.Bson.Serialization.Attributes;

namespace Canvas.Models;

public sealed class Board
{
    // The canonical, normalized board name is the document's _id (the boardId).
    [BsonId]
    public string Id { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime LastActivityAt { get; set; }
}
