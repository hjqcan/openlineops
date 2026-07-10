using OpenLineOps.Runner;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerCommandLineParserTests
{
    [Fact]
    public void ParseDefaultsToActiveSnapshot()
    {
        var result = RunnerCommandLineParser.Parse(["run", "C:/automation/line-a"]);

        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.NotNull(result.Options);
        Assert.Equal("C:/automation/line-a", result.Options.ProjectTarget);
        Assert.Equal("active", result.Options.Snapshot);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ParseAcceptsSnapshotAndTraceMetadata()
    {
        var result = RunnerCommandLineParser.Parse(
        [
            "run",
            "line-a/line-a.oloproj",
            "--snapshot=snapshot.release.42",
            "--serial",
            "SN-001",
            "--batch",
            "B-7",
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
        Assert.Equal("SN-001", options.SerialNumber);
        Assert.Equal("B-7", options.BatchId);
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
        var result = RunnerCommandLineParser.Parse(["run", "project", "--actor", "help"]);

        Assert.Equal(RunnerParseStatus.Run, result.Status);
        Assert.Equal("help", result.Options?.ActorId);
    }
}
