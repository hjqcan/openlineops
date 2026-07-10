using OpenLineOps.Domain.Abstractions.Serialization;

namespace OpenLineOps.Domain.Abstractions.Tests;

public sealed class CanonicalEnumTokenTests
{
    [Fact]
    public void TryParseAcceptsOnlyExactCanonicalName()
    {
        Assert.True(CanonicalEnumToken.TryParse<TestState>("Ready", out var canonical));
        Assert.Equal(TestState.Ready, canonical);

        Assert.False(CanonicalEnumToken.TryParse<TestState>("ready", out _));
        Assert.False(CanonicalEnumToken.TryParse<TestState>("1", out _));
        Assert.False(CanonicalEnumToken.TryParse<TestState>("Unknown", out _));
    }

    private enum TestState
    {
        Idle,
        Ready
    }
}
