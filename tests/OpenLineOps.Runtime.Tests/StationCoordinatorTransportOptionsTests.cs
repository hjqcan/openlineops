using OpenLineOps.Runtime.Infrastructure.Transport;

namespace OpenLineOps.Runtime.Tests;

public sealed class StationCoordinatorTransportOptionsTests
{
    [Fact]
    public void ResolveBrokerUriAcceptsDedicatedTlsCredentials()
    {
        var options = new StationCoordinatorTransportOptions
        {
            BrokerUri = "amqps://coordinator:coordinator-secret@rabbit.internal:5671/openlineops",
            RequireTls = true
        };

        var brokerUri = options.ResolveBrokerUri();

        Assert.Equal("amqps", brokerUri.Scheme);
        Assert.Equal("coordinator:coordinator-secret", brokerUri.UserInfo);
    }

    [Theory]
    [InlineData("amqps://rabbit.internal:5671/openlineops")]
    [InlineData("amqps://coordinator@rabbit.internal:5671/openlineops")]
    [InlineData("amqps://coordinator:@rabbit.internal:5671/openlineops")]
    [InlineData("amqps://guest:secret@rabbit.internal:5671/openlineops")]
    [InlineData("amqps://GUEST:secret@rabbit.internal:5671/openlineops")]
    public void ResolveBrokerUriRejectsTlsBrokerWithoutDedicatedCredentials(string brokerUri)
    {
        var options = new StationCoordinatorTransportOptions
        {
            BrokerUri = brokerUri,
            RequireTls = true
        };

        var exception = Assert.Throws<InvalidOperationException>(options.ResolveBrokerUri);

        Assert.Contains("non-guest username", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveBrokerUriAllowsCredentiallessLocalBrokerWhenTlsIsDisabled()
    {
        var options = new StationCoordinatorTransportOptions
        {
            BrokerUri = "amqp://localhost:5672",
            RequireTls = false
        };

        var brokerUri = options.ResolveBrokerUri();

        Assert.Equal("amqp", brokerUri.Scheme);
        Assert.Empty(brokerUri.UserInfo);
    }
}
