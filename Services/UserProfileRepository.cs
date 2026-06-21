using Canvas.Models;
using MongoDB.Driver;

namespace Canvas.Services;

public interface IUserProfileRepository
{
    Task<UserProfile> GetOrCreateProfileAsync(string userId, CancellationToken cancellationToken);

    Task SetDisplayNameAsync(string userId, string name, CancellationToken cancellationToken);

    Task<string?> GetDisplayNameAsync(string userId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(
        IEnumerable<string> userIds,
        CancellationToken cancellationToken);

    Task SetLastBoardAsync(string userId, string boardName, CancellationToken cancellationToken);

    Task<string?> GetLastBoardAsync(string userId, CancellationToken cancellationToken);
}

public sealed class UserProfileRepository : IUserProfileRepository, IHostedService
{
    private const string DefaultDisplayName = "Anonymous";
    private readonly Task<IMongoCollection<UserProfile>> _users;

    public UserProfileRepository(IMongoDbContext mongoDbContext)
    {
        _users = mongoDbContext.UsersAsync;
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

        var users = await _users;
        return await users.FindOneAndUpdateAsync(
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
        var users = await _users;
        var result = await users.UpdateOneAsync(
            profile => profile.UserId == userId,
            update,
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            await GetOrCreateProfileAsync(userId, cancellationToken);
            await users.UpdateOneAsync(
                profile => profile.UserId == userId,
                update,
                cancellationToken: cancellationToken);
        }
    }

    public async Task<string?> GetDisplayNameAsync(string userId, CancellationToken cancellationToken)
    {
        var users = await _users;
        var profile = await users.Find(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken);
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

        var users = await _users;
        var profiles = await users
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

        var users = await _users;
        var update = Builders<UserProfile>.Update.Set(profile => profile.LastBoardName, boardName);
        var result = await users.UpdateOneAsync(
            profile => profile.UserId == userId,
            update,
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            await GetOrCreateProfileAsync(userId, cancellationToken);
            await users.UpdateOneAsync(
                profile => profile.UserId == userId,
                update,
                cancellationToken: cancellationToken);
        }
    }

    public async Task<string?> GetLastBoardAsync(string userId, CancellationToken cancellationToken)
    {
        var users = await _users;
        var profile = await users.Find(p => p.UserId == userId).FirstOrDefaultAsync(cancellationToken);
        return profile?.LastBoardName;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var users = await _users;
        var userIdIndex = new CreateIndexModel<UserProfile>(
            Builders<UserProfile>.IndexKeys.Ascending(profile => profile.UserId),
            new CreateIndexOptions { Unique = true, Name = "ux_users_user_id" });

        await users.Indexes.CreateOneAsync(userIdIndex, cancellationToken: cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
