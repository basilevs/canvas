using Canvas.Services;

namespace Canvas.Tests;

[TestClass]
public sealed class BoardNameNormalizerTests
{
    [TestMethod]
    [DataRow("Board-1", "board-1")]
    [DataRow("  My_Board  ", "my_board")]
    [DataRow("abc123", "abc123")]
    public void TryNormalizeBoardName_ReturnsCanonicalSlug(string input, string expected)
    {
        var result = BoardNameNormalizer.TryNormalizeBoardName(input, out var normalized);

        Assert.IsTrue(result);
        Assert.AreEqual(expected, normalized);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("bad name")]
    [DataRow("!invalid")]
    public void TryNormalizeBoardName_RejectsInvalidSlugs(string input)
    {
        var result = BoardNameNormalizer.TryNormalizeBoardName(input, out var normalized);

        Assert.IsFalse(result);
        Assert.AreEqual(string.Empty, normalized);
    }
}
