using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerCommandLineParserTests
{
    [Fact]
    public void ParseDefaultsToActiveSnapshot()
    {
        var result = RunnerCommandLineParser.Parse(
            ["run", "C:/automation/line-a", "--production-unit", "UNIT-001", "--actor", "operator-a"]);

        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.NotNull(result.Options);
        Assert.Equal("C:/automation/line-a", result.Options.ProjectTarget);
        Assert.Equal("active", result.Options.Snapshot);
        Assert.NotEqual(Guid.Empty, result.Options.ProductionRunId);
        Assert.Equal("UNIT-001", result.Options.ProductionUnitIdentityValue);
        Assert.Equal("operator-a", result.Options.ActorId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ParseAcceptsProductionRunIdentityAndTraceMetadata()
    {
        var productionRunId = Guid.Parse("00000000-0000-0000-0000-000000000042");
        var result = RunnerCommandLineParser.Parse(
        [
            "run",
            "line-a/line-a.oloproj",
            "--snapshot=snapshot.release.42",
            "--run-id",
            productionRunId.ToString("D"),
            "--production-unit",
            "UNIT-001",
            "--lot",
            "LOT-7",
            "--carrier",
            "carrier-7",
            "--slot",
            "slot-2",
            "--fixture",
            "fixture-left",
            "--device",
            "scanner-01",
            "--actor",
            "operator-a"
        ]);

        var options = Assert.IsType<RunnerRunOptions>(result.Options);
        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.Equal("snapshot.release.42", options.Snapshot);
        Assert.Equal(productionRunId, options.ProductionRunId);
        Assert.Equal("UNIT-001", options.ProductionUnitIdentityValue);
        Assert.Equal("LOT-7", options.LotId);
        Assert.Equal("carrier-7", options.CarrierId);
        Assert.Equal("slot-2", options.SlotId);
        Assert.Equal("fixture-left", options.FixtureId);
        Assert.Equal("scanner-01", options.DeviceId);
        Assert.Equal("operator-a", options.ActorId);
    }

    [Theory]
    [InlineData("run", "project", "--unknown", "value", "Unknown option '--unknown'.")]
    [InlineData("run", "project", "--snapshot", "Missing value for '--snapshot'.")]
    [InlineData("run", "project", "extra", "Unexpected argument 'extra'.")]
    [InlineData("run", "project", "--snapshot", "one", "--snapshot", "two", "Option '--snapshot' may only be specified once.")]
    public void ParseRejectsInvalidArguments(params string[] argumentsAndExpectedError)
    {
        var expectedError = argumentsAndExpectedError[^1];
        var arguments = argumentsAndExpectedError[..^1];

        var result = RunnerCommandLineParser.Parse(arguments);

        Assert.Equal(RunnerParseStatus.Error, result.Status);
        Assert.Equal(expectedError, result.ErrorMessage);
        Assert.Null(result.Options);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("run", "--help")]
    [InlineData("help")]
    public void ParseRecognizesHelp(params string[] arguments)
    {
        var result = RunnerCommandLineParser.Parse(arguments);

        Assert.Equal(RunnerParseStatus.Help, result.Status);
    }

    [Fact]
    public void ParseDoesNotTreatMetadataValueNamedHelpAsHelpCommand()
    {
        var result = RunnerCommandLineParser.Parse(
            ["run", "project", "--production-unit", "UNIT-001", "--actor", "help"]);

        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.Equal("help", result.Options?.ActorId);
    }

    [Theory]
    [InlineData("run", "project", "--actor", "operator-a", "Option '--production-unit' is required.")]
    [InlineData("run", "project", "--production-unit", "UNIT-001", "Option '--actor' is required.")]
    [InlineData("run", "project", "--serial", "SN-001", "Unknown option '--serial'.")]
    [InlineData("run", "project", "--production-unit", " UNIT-001", "--actor", "operator-a", "Value for '--production-unit' must not have leading or trailing whitespace.")]
    [InlineData("run", "project", "--production-unit", "UNIT-001", "--actor", "operator-a", "--run-id", "42", "Value for '--run-id' must be a non-empty GUID in D format.")]
    [InlineData("run", "project", "--Production-Unit", "UNIT-001", "Unknown option '--Production-Unit'.")]
    [InlineData("Run", "project", "--production-unit", "UNIT-001", "--actor", "operator-a", "Unknown command 'Run'.")]
    public void ParseRejectsMissingOrNonCanonicalProductionRunIdentity(
        params string[] argumentsAndExpectedError)
    {
        var expectedError = argumentsAndExpectedError[^1];

        var result = RunnerCommandLineParser.Parse(argumentsAndExpectedError[..^1]);

        Assert.Equal(RunnerParseStatus.Error, result.Status);
        Assert.Equal(expectedError, result.ErrorMessage);
        Assert.Null(result.Options);
    }
}
