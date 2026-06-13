namespace Canvas.Services;

public static class BoardNameNormalizer
{
    private const int MinLength = 1;
    private const int MaxLength = 64;

    public static bool TryNormalizeBoardName(string? boardName, out string normalizedBoardName)
    {
        normalizedBoardName = string.Empty;

        if (string.IsNullOrWhiteSpace(boardName))
        {
            return false;
        }

        var candidate = boardName.Trim().ToLowerInvariant();
        if (candidate.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (!IsAllowedBoardNameCharacter(character))
            {
                return false;
            }
        }

        normalizedBoardName = candidate;
        return true;
    }

    private static bool IsAllowedBoardNameCharacter(char character)
    {
        return character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-'
            or '_';
    }
}
