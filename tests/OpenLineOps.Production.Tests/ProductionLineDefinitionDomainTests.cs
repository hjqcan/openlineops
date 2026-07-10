using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;

namespace OpenLineOps.Production.Tests;

public sealed class ProductionLineDefinitionDomainTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateOrdersContiguousStagesAndPreservesDutAndAdapterContract()
    {
        var definition = ProductionLineDefinition.Create(
            new ProductionLineDefinitionId("line.main"),
            "Main Line",
            "topology.main",
            DutModelDefinition.Create(new DutModelId("dut.model-a"), "MODEL-A", "serialNumber"),
            [Workstation()],
            [Stage("stage.test", 2, "flow.test", "adapter.test"), Stage("stage.load", 1, "flow.load")],
            [Adapter()],
            CreatedAtUtc);

        Assert.Equal(["stage.load", "stage.test"], definition.Stages.Select(stage => stage.Id.Value));
        Assert.Equal("MODEL-A", definition.DutModel.ModelCode);
        var adapter = Assert.Single(definition.ExternalTestProgramAdapters);
        Assert.Equal(ExternalTestProgramLaunchKind.Provider, adapter.LaunchKind);
        Assert.Equal("provider.test", adapter.ProviderKey);
        Assert.Equal(TimeSpan.FromSeconds(30), adapter.Timeout);
        Assert.Equal(["model", "serial"], adapter.InputMappings.Select(mapping => mapping.Target));
    }

    [Fact]
    public void CreateRejectsNonContiguousStageSequence()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProductionLineDefinition.Create(
            new ProductionLineDefinitionId("line.invalid"),
            "Invalid",
            "topology.main",
            DutModelDefinition.Create(new DutModelId("dut.model-a"), "MODEL-A", "serialNumber"),
            [Workstation()],
            [Stage("stage.one", 1, "flow.one"), Stage("stage.three", 3, "flow.three")],
            [],
            CreatedAtUtc));

        Assert.Contains("contiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExternalAdapterRejectsHostAbsoluteExecutableAndMissingDutMappings()
    {
        Assert.Throws<ArgumentException>(() => ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.absolute"),
            "Absolute",
            "test.execute",
            "Execute",
            "C:/tools/test.exe",
            null,
            [],
            ValidInputs(),
            ValidResults(),
            TimeSpan.FromSeconds(30)));

        var exception = Assert.Throws<ArgumentException>(() => ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.no-dut"),
            "No DUT",
            "test.execute",
            "Execute",
            null,
            "provider.test",
            [],
            [new ExternalTestProgramInputMapping("runtime.temperature", "temperature")],
            ValidResults(),
            TimeSpan.FromSeconds(30)));

        Assert.Contains("DUT identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("line.")]
    [InlineData("line/main")]
    public void LineDefinitionIdRejectsNonPortableDirectoryNames(string value)
    {
        Assert.Throws<ArgumentException>(() => new ProductionLineDefinitionId(value));
    }

    [Fact]
    public void ExternalAdapterRequiresWholeMillisecondTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.precision"),
            "Precision",
            "test.execute",
            "Execute",
            null,
            "provider.test",
            [],
            ValidInputs(),
            ValidResults(),
            TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void DefinitionRejectsUnusedExternalAdapter()
    {
        var exception = Assert.Throws<ArgumentException>(() => ProductionLineDefinition.Create(
            new ProductionLineDefinitionId("line.unused"),
            "Unused",
            "topology.main",
            DutModelDefinition.Create(new DutModelId("dut.model-a"), "MODEL-A", "serialNumber"),
            [Workstation()],
            [Stage("stage.load", 1, "flow.load")],
            [Adapter()],
            CreatedAtUtc));

        Assert.Contains("must be used", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal static WorkstationDefinition Workstation()
    {
        return WorkstationDefinition.Create(
            new WorkstationId("workstation.eol"),
            "EOL",
            "station.eol",
            "system.tester");
    }

    internal static ProcessStage Stage(
        string id,
        int sequence,
        string flowId,
        string? adapterId = null)
    {
        return ProcessStage.Create(
            new ProcessStageId(id),
            sequence,
            id,
            new WorkstationId("workstation.eol"),
            flowId,
            adapterId is null ? null : new ExternalTestProgramAdapterId(adapterId));
    }

    internal static ExternalTestProgramAdapter Adapter()
    {
        return ExternalTestProgramAdapter.Create(
            new ExternalTestProgramAdapterId("adapter.test"),
            "External EOL",
            "test.external",
            "ExecuteTestProgram",
            null,
            "provider.test",
            ["--serial", "{{dut.identity}}"],
            ValidInputs(),
            ValidResults(),
            TimeSpan.FromSeconds(30));
    }

    internal static ExternalTestProgramInputMapping[] ValidInputs() =>
    [
        new ExternalTestProgramInputMapping(ExternalTestProgramInputSources.DutIdentity, "serial"),
        new ExternalTestProgramInputMapping(ExternalTestProgramInputSources.DutModel, "model")
    ];

    internal static ExternalTestProgramResultMapping[] ValidResults() =>
    [new ExternalTestProgramResultMapping("$.outcome", "test.outcome")];
}
