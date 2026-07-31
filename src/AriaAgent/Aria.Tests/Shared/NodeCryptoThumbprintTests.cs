using Aria.Shared;
using Xunit;

namespace Aria.Tests.Shared;

public class NodeCryptoThumbprintTests
{
    [Theory]
    [InlineData("abcdefghijklmnop", "abcd-efgh-ijkl-mnop")]
    [InlineData("ABCD-efgh-ijkl-mnop", "ABCD-efgh-ijkl-mnop")]
    [InlineData("abc", "abc")]
    [InlineData("", "")]
    public void GroupThumbprint_InsertsDashesEveryFour(string input, string expected) =>
        Assert.Equal(expected, NodeCrypto.GroupThumbprint(input));
}
