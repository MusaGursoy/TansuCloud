// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using FluentAssertions;
using TansuCloud.Storage.Services;

public class StorageEtagsTests
{
    [Fact]
    public void ComputeWeak_IsStable_ForSameContent()
    {
        var a1 = StorageEtags.ComputeWeak("hello");
        var a2 = StorageEtags.ComputeWeak("hello");
        a1.Should().Be(a2);
        a1.Should().StartWith("W/\"").And.EndWith("\"");
    }

    [Fact]
    public void ComputeWeak_Differs_ForDifferentContent()
    {
        var a = StorageEtags.ComputeWeak("hello");
        var b = StorageEtags.ComputeWeak("world");
        a.Should().NotBe(b);
    }
}
