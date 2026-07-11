using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerCommandLineParserTests
{
    [Fact]
    public void ParseDefaultsToActiveSnapshot()
    {
        var result = RunnerCommandLineParser.Parse(
            ["run", "C:/automation/line-a", "--production-unit-id", "00000000-0000-0000-0000-000000000001", "--identity", "UNIT-001", "--actor", "operator-a"]);

        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.NotNull(result.Options);
        Assert.Equal("C:/automation/line-a", result.Options.ProjectTarget);
        Assert.Equal("active", result.Options.Snapshot);
        Assert.NotEqual(Guid.Empty, result.Options.ProductionRunId);
        Assert.NotEqual(Guid.Empty, result.Options.ProductionUnitId);
        Assert.Equal("UNIT-001", result.Options.ProductionUnitIdentityValue);
        Assert.Equal("operator-a", result.Options.ActorId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ParseAcceptsProductionRunIdentityAndTraceMetadata()
    {
        var productionRunId = Guid.Parse("00000000-0000-0000-0000-000000000042");
        var productionUnitId = Guid.Parse("00000000-0000-0000-0000-000000000043");
        var result = RunnerCommandLineParser.Parse(
        [
            "run",
            "line-a/line-a.oloproj",
            "--snapshot=snapshot.release.42",
            "--run-id",
            productionRunId.ToString("D"),
            "--production-unit-id",
            productionUnitId.ToString("D"),
            "--identity",
            "UNIT-001",
            "--actor",
            "operator-a"
        ]);

        var options = Assert.IsType<RunnerRunOptions>(result.Options);
        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.Equal("snapshot.release.42", options.Snapshot);
        Assert.Equal(productionRunId, options.ProductionRunId);
        Assert.Equal(productionUnitId, options.ProductionUnitId);
        Assert.Equal("UNIT-001", options.ProductionUnitIdentityValue);
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
            ["run", "project", "--production-unit-id", "00000000-0000-0000-0000-000000000001", "--identity", "UNIT-001", "--actor", "help"]);

        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.Equal("help", result.Options?.ActorId);
    }

    [Theory]
    [InlineData("run", "project", "--production-unit-id", "00000000-0000-0000-0000-000000000001", "--actor", "operator-a", "Option '--identity' is required.")]
    [InlineData("run", "project", "--production-unit-id", "00000000-0000-0000-0000-000000000001", "--identity", "UNIT-001", "Option '--actor' is required.")]
    [InlineData("run", "project", "--serial", "SN-001", "Unknown option '--serial'.")]
    [InlineData("run", "project", "--production-unit-id", "00000000-0000-0000-0000-000000000001", "--identity", " UNIT-001", "--actor", "operator-a", "Value for '--identity' must not have leading or trailing whitespace.")]
    [InlineData("run", "project", "--production-unit-id", "00000000-0000-0000-0000-000000000001", "--identity", "UNIT-001", "--actor", "operator-a", "--run-id", "42", "Value for '--run-id' must be a non-empty GUID in D format.")]
    [InlineData("run", "project", "--Production-Unit-Id", "00000000-0000-0000-0000-000000000001", "Unknown option '--Production-Unit-Id'.")]
    [InlineData("Run", "project", "--production-unit-id", "00000000-0000-0000-0000-000000000001", "--identity", "UNIT-001", "--actor", "operator-a", "Unknown command 'Run'.")]
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
