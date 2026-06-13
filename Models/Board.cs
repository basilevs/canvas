using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Canvas.Models;

public sealed class Board
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime LastActivityAt { get; set; }
}
