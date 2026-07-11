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
              "measuredVoltage":{"kind":"FixedPoint","value":"3.30"},
              "accepted":{"kind":"Boolean","value":"true"}
            }
            """,
            ExecutionStatus.Completed,
            ResultJudgement.Passed,
            []);

        var outputs = ProductionContextOutputReader.Read(payload);

        Assert.Equal(2, outputs.Count);
        Assert.Equal("3.30", outputs["measuredVoltage"].CanonicalValue);
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

    [Fact]
    public void RejectsSameOutputKeyFromMultipleCommands()
    {
        var payload = "{\"accepted\":{\"kind\":\"Boolean\",\"value\":\"true\"}}";

        Assert.Throws<InvalidDataException>(() =>
            ProductionContextOutputReader.ReadMany([payload, payload]));
    }
}
