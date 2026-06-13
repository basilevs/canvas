using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Canvas.Models;

public sealed class UserProfile
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = "Anonymous";

    public string? LastBoardName { get; set; }

    public DateTime CreatedAt { get; set; }
}
