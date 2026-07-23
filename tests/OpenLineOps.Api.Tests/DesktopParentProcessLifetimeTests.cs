using OpenLineOps.Api;

namespace OpenLineOps.Api.Tests;

public sealed class DesktopParentProcessLifetimeTests
{
    [Fact]
    public void NoDesktopBindingLeavesParentLifetimeDisabled()
    {
        Assert.Null(DesktopParentProcessLifetime.ParseConfiguredProcessId(null, false));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", false)]
    public void ParentLifetimeAndHandshakeMustBeConfiguredTogether(
        string? configuredValue,
        bool handshakeConfigured)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DesktopParentProcessLifetime.ParseConfiguredProcessId(
                configuredValue,
                handshakeConfigured));

        Assert.Contains("configured together", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("2147483648")]
    public void ParentProcessIdentityMustBeCanonical(string configuredValue)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DesktopParentProcessLifetime.ParseConfiguredProcessId(
                configuredValue,
                true));
    }

    [Fact]
    public void CanonicalParentProcessIdentityIsAccepted()
    {
        Assert.Equal(
            12345,
            DesktopParentProcessLifetime.ParseConfiguredProcessId("12345", true));
    }
}
