using OpenLineOps.Projects.Application.Releases;

namespace OpenLineOps.Projects.Tests;

public sealed class ProjectReleaseExternalTestProgramContractTests
{
    [Theory]
    [InlineData("$dut.identity")]
    [InlineData("$dut.model")]
    [InlineData("$dut.inputKey")]
    [InlineData("$run.id")]
    [InlineData("$line.id")]
    [InlineData("$stage.id")]
    [InlineData("$stage.sequence")]
    [InlineData("$workstation.id")]
    [InlineData("$session.id")]
    [InlineData("$station.id")]
    [InlineData("$configuration.id")]
    [InlineData("$step.id")]
    [InlineData("$command.id")]
    [InlineData("$command.name")]
    [InlineData("$node.id")]
    [InlineData("$action.id")]
    [InlineData("$capability.id")]
    [InlineData("$project.id")]
    [InlineData("$application.id")]
    [InlineData("$snapshot.id")]
    [InlineData("$target.kind")]
    [InlineData("$target.id")]
    public void SupportedInputSourcesAreExact(string source)
    {
        Assert.True(ProjectReleaseExternalTestProgramContract.IsSupportedInputSource(source));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedInputSource(source.ToUpperInvariant()));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedInputSource($" {source}"));
    }

    [Fact]
    public void ArgumentTemplatesAcceptOnlyFrozenRuntimeOrMappedInputPlaceholders()
    {
        Assert.True(ProjectReleaseExternalTestProgramContract.IsSupportedArgumentTemplate(
            "--dut={{dut.identity}}",
            ["serial"]));
        Assert.True(ProjectReleaseExternalTestProgramContract.IsSupportedArgumentTemplate(
            "{{input.serial}}",
            ["serial"]));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedArgumentTemplate(
            "{{input.other}}",
            ["serial"]));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedArgumentTemplate(
            "{{unknown.value}}",
            ["serial"]));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedArgumentTemplate(
            "{{dut.identity}",
            ["serial"]));
    }

    [Theory]
    [InlineData("$.outcome", true)]
    [InlineData("$.metrics.voltage", true)]
    [InlineData("$", false)]
    [InlineData("$.metrics..voltage", false)]
    [InlineData(" $.outcome", false)]
    public void ResultPathsUseExactObjectPropertySegments(string sourcePath, bool expected)
    {
        Assert.Equal(
            expected,
            ProjectReleaseExternalTestProgramContract.IsSupportedResultPath(sourcePath));
    }

    [Fact]
    public void OutcomeMappingRequiresExactDistinctCanonicalTokens()
    {
        Assert.True(ProjectReleaseExternalTestProgramContract.IsSupportedOutcomeMapping(
            new ProjectReleaseExternalTestProgramOutcomeMapping(
                "$.judgement",
                "Passed",
                "Failed",
                "Aborted")));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedOutcomeMapping(
            new ProjectReleaseExternalTestProgramOutcomeMapping(
                "$.judgement",
                "Passed",
                "Passed",
                "Aborted")));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedOutcomeMapping(
            new ProjectReleaseExternalTestProgramOutcomeMapping(
                "$.judgement",
                " Passed",
                "Failed",
                "Aborted")));
        Assert.False(ProjectReleaseExternalTestProgramContract.IsSupportedOutcomeMapping(
            new ProjectReleaseExternalTestProgramOutcomeMapping(
                "$.Judgement",
                "Passed",
                "Failed",
                "Aborted") with { SourcePath = "$.judgement " }));
    }
}
