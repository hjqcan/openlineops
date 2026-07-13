using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Tests;

public sealed class StationTransportRouteTests
{
    [Fact]
    public void DottedStationIdentityIsHashedIntoOneCanonicalTopicSegment()
    {
        const string expectedStationSegment =
            "6b138c6f0ce998fd6171f4c6ccbf9a304af082f5823cb6187382115d72febf3b";

        var route = StationTransportRoute.Event(
            "station.main.physical",
            "StationJobCompleted");

        Assert.Equal(
            $"station.{expectedStationSegment}.StationJobCompleted",
            route);
        Assert.Equal(3, route.Split('.').Length);
        Assert.DoesNotContain("station.main.physical", route, StringComparison.Ordinal);
        Assert.Equal(
            "station.*.StationJobCompleted",
            StationTransportRoute.EventPattern("StationJobCompleted"));
    }

    [Theory]
    [InlineData("kind.with.dot")]
    [InlineData("kind*")]
    [InlineData("kind#")]
    public void TopicKindMustRemainOneLiteralSegment(string kind)
    {
        Assert.Throws<ArgumentException>(() =>
            StationTransportRoute.Event("station.main", kind));
        Assert.Throws<ArgumentException>(() =>
            StationTransportRoute.EventPattern(kind));
    }
}
