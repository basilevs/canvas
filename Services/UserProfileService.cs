using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

public interface IUserProfileService
{
    Task<UserProfile> GetOrCreateProfileAsync(string userId, CancellationToken cancellationToken);

    Task SetDisplayNameAsync(string userId, string name, CancellationToken cancellationToken);

    Task<string?> GetDisplayNameAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken);

    Task SetLastBoardAsync(string userId, string boardName, CancellationToken cancellationToken);

    Task<string?> GetLastBoardAsync(string userId, CancellationToken cancellationToken);

    Task EnsureIndexesAsync(CancellationToken cancellationToken);
}

public sealed class UserProfileService : IUserProfileService
{
    private const string DefaultDisplayName = "Anonymous";
    private readonly IMongoCollection<UserProfile> _users;

    public UserProfileService(IMongoDbContext mongoDbContext)
    {
        _users = mongoDbContext.Users;
    }

    public async Task<UserProfile> GetOrCreateProfileAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        var update = Builders<UserProfile>.Update
            .SetOnInsert(profile => profile.UserId, userId)
            .SetOnInsert(profile => profile.DisplayName, DefaultDisplayName)
            .SetOnInsert(profile => profile.CreatedAt, DateTime.UtcNow);

        var options = new FindOneAndUpdateOptions<UserProfile>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };

        return await _users.FindOneAndUpdateAsync(
            profile => profile.UserId == userId,
            update,
            options,
            cancellationToken);
    }

    public async Task SetDisplayNameAsync(string userId, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Display name is required.", nameof(name));
        }

        var update = Builders<UserProfile>.Update.Set(profile => profile.DisplayName, name);
        var result = await _users.UpdateOneAsync(
            profile => profile.UserId == userId,
            update,
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            await GetOrCreateProfileAsync(userId, cancellationToken);
            await _users.UpdateOneAsync(
                profile => profile.UserId == userId,
                update,
                cancellationToken: cancellationToken);
        }
    }

    public async Task<string?> GetDisplayNameAsync(string userId, CancellationToken cancellationToken)
    {
        var profile = await _users.Find(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        return profile?.DisplayName;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken)
    {
        var idList = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (idList.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var profiles = await _users
            .Find(profile => idList.Contains(profile.UserId))
            .ToListAsync(cancellationToken);

        var names = profiles.ToDictionary(
            profile => profile.UserId,
            profile => profile.DisplayName,
            StringComparer.Ordinal);

        foreach (var userId in idList)
        {
            names.TryAdd(userId, DefaultDisplayName);
        }

        return names;
    }

    public async Task SetLastBoardAsync(string userId, string boardName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(boardName))
        {
            throw new ArgumentException("Board name is required.", nameof(boardName));
        }

        var update = Builders<UserProfile>.Update.Set(profile => profile.LastBoardName, boardName);
        var result = await _users.UpdateOneAsync(
            profile => profile.UserId == userId,
            update,
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            await GetOrCreateProfileAsync(userId, cancellationToken);
            await _users.UpdateOneAsync(
                profile => profile.UserId == userId,
                update,
                cancellationToken: cancellationToken);
        }
    }

    public async Task<string?> GetLastBoardAsync(string userId, CancellationToken cancellationToken)
    {
        var profile = await _users.Find(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        return profile?.LastBoardName;
    }

    public Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var userIdIndex = new CreateIndexModel<UserProfile>(
            Builders<UserProfile>.IndexKeys.Ascending(profile => profile.UserId),
            new CreateIndexOptions { Unique = true, Name = "ux_users_user_id" });

        return _users.Indexes.CreateOneAsync(userIdIndex, cancellationToken: cancellationToken);
    }
}
