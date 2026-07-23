using OpenLineOps.Runtime.Application.Commands;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Runtime.Tests;

public sealed class ProductionContextOutputReaderTests
{
    [Fact]
    public void ReadsOnlyExactTypedValuesAndIgnoresValidatedReservedEvidence()
    {
        var payload = RuntimeCommandEvidencePayload.Attach(
            """
            {
              "measuredVoltage":{"kind":"FixedPoint","value":"3.3"},
              "accepted":{"kind":"Boolean","value":"true"}
            }
            """,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            []);

        var outputs = ProductionContextOutputReader.Read(payload);

        Assert.Equal(2, outputs.Count);
        Assert.Equal("3.3", outputs["measuredVoltage"].CanonicalValue);
        Assert.Equal(ProductionContextValueKind.Boolean, outputs["accepted"].Kind);
    }

    [Theory]
    [InlineData("{\"measuredVoltage\":3.3}")]
    [InlineData("{\"measuredVoltage\":{\"kind\":\"FixedPoint\",\"value\":3.3}}")]
    [InlineData("{\"measuredVoltage\":{\"kind\":\"fixedPoint\",\"value\":\"3.30\"}}")]
    [InlineData("{\"measuredVoltage\":{\"kind\":\"FixedPoint\",\"value\":\"3.30\"},\"measuredVoltage\":{\"kind\":\"FixedPoint\",\"value\":\"3.31\"}}")]
    public void RejectsOrdinaryInferredOrAmbiguousJson(string payload)
    {
        Assert.Throws<InvalidDataException>(() => ProductionContextOutputReader.Read(payload));
    }

    [Theory]
    [InlineData("{\"measurement\":{\"kind\":\"FixedPoint\",\"value\":\"3.30\"}}")]
    [InlineData("{\"completedAtUtc\":{\"kind\":\"DateTimeUtc\",\"value\":\"2026-07-15T00:00:00.0000000Z\"}}")]
    public void StationAndPythonTypedOutputBoundaryRejectsNonCanonicalValues(string payload)
    {
        Assert.Throws<InvalidDataException>(() =>
            ProductionContextOutputReader.ReadExplicitMany([payload]));
    }

    [Fact]
    public void RejectsSameOutputKeyFromMultipleCommands()
    {
        var payload = "{\"accepted\":{\"kind\":\"Boolean\",\"value\":\"true\"}}";

        Assert.Throws<InvalidDataException>(() =>
            ProductionContextOutputReader.ReadMany([payload, payload]));
    }

    [Theory]
    [InlineData(ProductionContextValueKind.FixedPoint, "3.50")]
    [InlineData(ProductionContextValueKind.FixedPoint, "03.5")]
    [InlineData(ProductionContextValueKind.FixedPoint, "+3.5")]
    [InlineData(ProductionContextValueKind.FixedPoint, ".5")]
    [InlineData(ProductionContextValueKind.FixedPoint, "3.")]
    [InlineData(ProductionContextValueKind.FixedPoint, "-0")]
    [InlineData(ProductionContextValueKind.DateTimeUtc, "2026-07-15T00:00:00.0000000Z")]
    public void RejectsNonUniquePersistedContextRepresentations(
        ProductionContextValueKind kind,
        string value)
    {
        Assert.Throws<ArgumentException>(() => new ProductionContextValue(kind, value));
    }

    [Theory]
    [InlineData(ProductionContextValueKind.FixedPoint, "0")]
    [InlineData(ProductionContextValueKind.FixedPoint, "3.5")]
    [InlineData(ProductionContextValueKind.FixedPoint, "-0.5")]
    [InlineData(
        ProductionContextValueKind.DateTimeUtc,
        "2026-07-15T00:00:00.0000000+00:00")]
    public void AcceptsUniquePersistedContextRepresentations(
        ProductionContextValueKind kind,
        string value)
    {
        var contextValue = new ProductionContextValue(kind, value);

        Assert.Equal(value, contextValue.CanonicalValue);
    }
}
