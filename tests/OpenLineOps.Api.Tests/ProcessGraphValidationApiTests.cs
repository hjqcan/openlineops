using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenLineOps.Api.Tests;

public sealed class ProcessGraphValidationApiTests : IDisposable
{
    private static readonly string[] ValidationTargetKinds = ["Graph", "Node", "Transition"];
    private readonly string _projectDirectory = Path.Combine(
        Path.GetTempPath(),
        "openlineops-process-validation-api-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidationContractPreservesExactGraphNodeAndTransitionTargets()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"project-{suffix}";
        var applicationId = $"application-{suffix}";
        var processId = $"process-{suffix}";
        var processesPath = $"/api/automation-projects/{projectId}/applications/{applicationId}/processes";
        using var factory = ApiTestAuthentication.CreateFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var workspace = await client.PostAsJsonAsync("/api/automation-project-workspaces", new
        {
            projectId,
            displayName = "Process Validation Contract",
            projectPath = _projectDirectory,
            defaultApplicationId = applicationId,
            defaultApplicationName = "Main"
        });
        Assert.Equal(HttpStatusCode.Created, workspace.StatusCode);

        using var created = await client.PostAsJsonAsync(processesPath, new
        {
            processDefinitionId = processId,
            versionId = $"{processId}@1",
            displayName = "Invalid Flow",
            nodes = new[]
            {
                new
                {
                    nodeId = "inspect",
                    kind = "Command",
                    displayName = "Inspect",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    targetKind = (string?)"Capability",
                    targetId = (string?)"vision.inspect",
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "inspect-to-missing",
                    fromNodeId = "inspect",
                    toNodeId = "missing",
                    label = (string?)null,
                    loopPolicy = "None",
                    maxTraversals = (int?)null
                }
            }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var validation = await client.GetAsync($"{processesPath}/{processId}/validation");
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        using var body = await JsonDocument.ParseAsync(await validation.Content.ReadAsStreamAsync());
        var issues = body.RootElement.GetProperty("issues").EnumerateArray().ToArray();

        AssertIssue(issues, "Processes.GraphStartNodeCountInvalid", "Graph", processId);
        AssertIssue(issues, "Processes.CommandCapabilityMissing", "Node", "inspect");
        AssertIssue(issues, "Processes.TransitionTargetMissing", "Transition", "inspect-to-missing");
        Assert.All(issues, issue =>
        {
            Assert.Contains(issue.GetProperty("targetKind").GetString(), ValidationTargetKinds);
            Assert.False(string.IsNullOrWhiteSpace(issue.GetProperty("targetId").GetString()));
        });
    }

    private static void AssertIssue(
        IEnumerable<JsonElement> issues,
        string code,
        string targetKind,
        string targetId)
    {
        var issue = Assert.Single(issues, candidate => candidate.GetProperty("code").GetString() == code);
        Assert.Equal(targetKind, issue.GetProperty("targetKind").GetString());
        Assert.Equal(targetId, issue.GetProperty("targetId").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDirectory))
        {
            Directory.Delete(_projectDirectory, recursive: true);
        }
    }
}
