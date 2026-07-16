using OpenLineOps.Agent.Contracts;

namespace OpenLineOps.Agent.Tests;

public sealed class StationTransportRouteTests
{
    [Fact]
    public void DottedStationIdentityIsHashedIntoOneCanonicalTopicSegment()
    {
        const string expectedStationSegment =
            "5f21e84917bdd14ee0269b0e09a3c39d15531f93ce99bfd3bb0b82fb9dda9a89";

        var route = StationTransportRoute.Event(
            "agent.main",
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
            StationTransportRoute.Event("agent.main", "station.main", kind));
        Assert.Throws<ArgumentException>(() =>
            StationTransportRoute.EventPattern(kind));
    }

    [Fact]
    public void CompositeIdentitiesCannotCollideOrLeakIntoTransportNames()
    {
        var first = Routes("a.b", "c");
        var second = Routes("a", "b.c");

        Assert.Equal(first, Routes("a.b", "c"));
        Assert.Equal(first.Length, first.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(second.Length, second.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));
        Assert.All(first, route =>
        {
            Assert.DoesNotContain("a.b", route, StringComparison.Ordinal);
            Assert.DoesNotContain(".c.", route, StringComparison.Ordinal);
        });
        Assert.All(second, route =>
        {
            Assert.DoesNotContain("b.c", route, StringComparison.Ordinal);
            Assert.DoesNotContain("station.a.", route, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("agent/main", "station.main")]
    [InlineData("agent.main", "station/main")]
    public void TransportIdentityRejectsPathSeparators(string agentId, string stationId)
    {
        Assert.Throws<ArgumentException>(() =>
            StationTransportRoute.TargetSegment(agentId, stationId));
    }

    private static string[] Routes(string agentId, string stationId) =>
    [
        StationTransportRoute.Job(agentId, stationId),
        StationTransportRoute.ResourceLeaseChanged(agentId, stationId),
        StationTransportRoute.Safety(agentId, stationId, "emergency-stop"),
        StationTransportRoute.JobQueue(agentId, stationId),
        StationTransportRoute.SafetyQueue(agentId, stationId, "emergency-stop"),
        StationTransportRoute.Event(agentId, stationId, nameof(StationJobCompleted))
    ];
}
