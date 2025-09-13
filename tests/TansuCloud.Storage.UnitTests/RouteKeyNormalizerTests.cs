// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using TansuCloud.Storage.Services;
using Xunit;

namespace TansuCloud.Storage.UnitTests;

public class RouteKeyNormalizerTests
{
    [Theory]
    [InlineData("img%2Foriginal.png", "img/original.png")]
    [InlineData("folder%2Fsub%2Ffile.txt", "folder/sub/file.txt")]
    [InlineData("space%20name.txt", "space name.txt")]
    public void Normalize_Unescapes_Percent_Encoded_Sequences(string input, string expected)
    {
        RouteKeyNormalizer.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_Is_Idempotent_For_Already_Unescaped()
    {
        var key = "a/b/c.txt";
        RouteKeyNormalizer.Normalize(key).Should().Be(key);
    }
}
