using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace OpenLineOps.Api.Tests;

public sealed class ProcessDefinitionsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ProcessDefinitionsApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task CreateProcessDefinitionReturnsCreatedAndCanBeQueried()
    {
        var processDefinitionId = NewProcessDefinitionId("create-query");

        using var response = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));

        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains($"/api/process-definitions/{processDefinitionId}", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(processDefinitionId, body.RootElement.GetProperty("processDefinitionId").GetString());
        Assert.Equal("Draft", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.Equal(2, body.RootElement.GetProperty("transitions").GetArrayLength());

        using var queryResponse = await _client.GetAsync($"/api/process-definitions/{processDefinitionId}");
        using var queryBody = await ReadJsonAsync(queryResponse);

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(processDefinitionId, queryBody.RootElement.GetProperty("processDefinitionId").GetString());
        Assert.Equal("packaging-line-eol@1.0.0", queryBody.RootElement.GetProperty("versionId").GetString());
    }

    [Fact]
    public async Task CreatePythonScriptProcessDefinitionDefaultsToBlocklyAndReturnsScriptTraceFields()
    {
        var processDefinitionId = NewProcessDefinitionId("python-script-create-query");

        using var response = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreatePythonScriptDefinitionRequest(processDefinitionId));
        using var body = await ReadJsonAsync(response);
        var scriptNode = body.RootElement
            .GetProperty("nodes")
            .EnumerateArray()
            .Single(node => node.GetProperty("kind").GetString() == "PythonScript");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Python", scriptNode.GetProperty("scriptLanguage").GetString());
        Assert.Equal("Blockly", scriptNode.GetProperty("scriptEditorMode").GetString());
        Assert.Equal("""{"blocks":{"languageVersion":0}}""", scriptNode.GetProperty("blocklyWorkspaceJson").GetString());
        Assert.Equal("result = {'normalized': input_payload}", scriptNode.GetProperty("scriptSourceCode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(scriptNode.GetProperty("scriptSourceHash").GetString()));
        Assert.Equal("7", scriptNode.GetProperty("scriptVersion").GetString());
        Assert.Equal(15, scriptNode.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task CreatePythonScriptProcessDefinitionRejectsInvalidEditorMode()
    {
        var processDefinitionId = NewProcessDefinitionId("python-script-invalid-editor");

        using var response = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreatePythonScriptDefinitionRequest(processDefinitionId, scriptEditorMode: "VisualBlocks"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PublishPythonScriptProcessDefinitionRejectsInvalidPythonSyntax()
    {
        var processDefinitionId = NewProcessDefinitionId("python-script-invalid-syntax");

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreatePythonScriptDefinitionRequest(
                processDefinitionId,
                scriptSourceCode: "if True\n    result = 1"));
        using var publishResponse = await _client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        using var publishBody = await ReadJsonAsync(publishResponse);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, publishResponse.StatusCode);
        Assert.Equal(
            "Validation.Processes.PythonScriptValidationFailed",
            publishBody.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ValidateInvalidProcessDefinitionReturnsValidationIssues()
    {
        var processDefinitionId = NewProcessDefinitionId("invalid-graph");

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            new
            {
                processDefinitionId,
                versionId = "packaging-line-eol@1.0.0",
                displayName = "Invalid Graph",
                nodes = new[]
                {
                    new
                    {
                        nodeId = "inspect",
                        kind = "Command",
                        displayName = "Inspect",
                        requiredCapability = (string?)null,
                        commandName = (string?)null,
                        timeoutSeconds = (int?)null,
                        inputPayload = (string?)null
                    }
                },
                transitions = Array.Empty<object>()
            });

        using var response = await _client.GetAsync($"/api/process-definitions/{processDefinitionId}/validation");
        using var body = await ReadJsonAsync(response);
        var issues = body.RootElement.GetProperty("issues");

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.RootElement.GetProperty("isValid").GetBoolean());
        Assert.Contains(issues.EnumerateArray(), issue => issue.GetProperty("code").GetString() == "Processes.GraphStartNodeCountInvalid");
        Assert.Contains(issues.EnumerateArray(), issue => issue.GetProperty("code").GetString() == "Processes.CommandCapabilityMissing");
    }

    [Fact]
    public async Task PublishValidProcessDefinitionMarksItPublished()
    {
        var processDefinitionId = NewProcessDefinitionId("publish");

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));
        using var response = await _client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        using var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Published", body.RootElement.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, body.RootElement.GetProperty("publishedAtUtc").ValueKind);
    }

    [Fact]
    public async Task PublishInvalidProcessDefinitionReturnsBadRequest()
    {
        var processDefinitionId = NewProcessDefinitionId("publish-invalid");

        using var createResponse = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            new
            {
                processDefinitionId,
                versionId = "packaging-line-eol@1.0.0",
                displayName = "Publish Invalid Graph",
                nodes = new[]
                {
                    new
                    {
                        nodeId = "inspect",
                        kind = "Command",
                        displayName = "Inspect",
                        requiredCapability = "vision-camera",
                        commandName = (string?)null,
                        timeoutSeconds = (int?)null,
                        inputPayload = (string?)null
                    }
                },
                transitions = Array.Empty<object>()
            });
        using var response = await _client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateDuplicateProcessDefinitionReturnsConflict()
    {
        var processDefinitionId = NewProcessDefinitionId("duplicate");

        using var firstResponse = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));
        using var secondResponse = await _client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
    }

    [Fact]
    public async Task GetMissingProcessDefinitionReturnsNotFound()
    {
        using var response = await _client.GetAsync($"/api/process-definitions/{NewProcessDefinitionId("missing")}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PublishedProcessDefinitionCanStartRuntimeSession()
    {
        using var factory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var processDefinitionId = NewProcessDefinitionId("runtime-start");
        var suffix = Guid.NewGuid().ToString("N");
        var recipeId = $"recipe-process-runtime-{suffix}";
        var stationProfileId = $"station-process-runtime-{suffix}";
        var projectId = $"project-process-runtime-{suffix}";
        var configurationSnapshotId = $"snapshot-process-runtime-{suffix}";

        using var createResponse = await client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));
        using var publishResponse = await client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            projectId,
            configurationSnapshotId,
            processDefinitionId,
            client);
        using var startResponse = await client.PostAsJsonAsync(
            $"/api/process-definitions/{processDefinitionId}/runtime-sessions",
            new
            {
                configurationSnapshotId
            });
        using var startBody = await ReadJsonAsync(startResponse);
        var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Contains($"/api/runtime/sessions/{sessionId}", startResponse.Headers.Location?.OriginalString, StringComparison.Ordinal);
        Assert.Equal(configurationSnapshotId, startBody.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, startBody.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(1, startBody.RootElement.GetProperty("commandCount").GetInt32());

        using var queryResponse = await client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionBody = await ReadJsonAsync(queryResponse);

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(processDefinitionId, sessionBody.RootElement.GetProperty("processDefinitionId").GetString());
        Assert.Equal("packaging-line-eol@1.0.0", sessionBody.RootElement.GetProperty("processVersionId").GetString());
        Assert.Equal(configurationSnapshotId, sessionBody.RootElement.GetProperty("configurationSnapshotId").GetString());
        Assert.Equal($"{recipeId}@1.0.0", sessionBody.RootElement.GetProperty("recipeSnapshotId").GetString());
        Assert.Equal(stationProfileId, sessionBody.RootElement.GetProperty("stationId").GetString());
        Assert.Equal("Completed", sessionBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("Inspect", sessionBody.RootElement.GetProperty("commands")[0].GetProperty("commandName").GetString());
    }

    [Fact]
    public async Task PublishedProcessDefinitionWithDecisionBranchCanStartRuntimeSession()
    {
        using var factory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var processDefinitionId = NewProcessDefinitionId("runtime-decision-branch");
        var suffix = Guid.NewGuid().ToString("N");
        var recipeId = $"recipe-process-runtime-branch-{suffix}";
        var stationProfileId = $"station-process-runtime-branch-{suffix}";
        var projectId = $"project-process-runtime-branch-{suffix}";
        var configurationSnapshotId = $"snapshot-process-runtime-branch-{suffix}";

        using var createResponse = await client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateBranchingDefinitionRequest(processDefinitionId));
        using var publishResponse = await client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            projectId,
            configurationSnapshotId,
            processDefinitionId,
            client);
        using var startResponse = await client.PostAsJsonAsync(
            $"/api/process-definitions/{processDefinitionId}/runtime-sessions",
            new
            {
                configurationSnapshotId
            });
        using var startBody = await ReadJsonAsync(startResponse);
        var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, startBody.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(2, startBody.RootElement.GetProperty("commandCount").GetInt32());

        using var queryResponse = await client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionBody = await ReadJsonAsync(queryResponse);
        var commandNames = sessionBody.RootElement
            .GetProperty("commands")
            .EnumerateArray()
            .Select(command => command.GetProperty("commandName").GetString()!)
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal(["ReadResult", "OpenPassGate"], commandNames);
        Assert.DoesNotContain("OpenFailGate", commandNames);
    }

    [Fact]
    public async Task PublishedProcessDefinitionWithCountedLoopPolicyCanStartRuntimeSession()
    {
        using var factory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var processDefinitionId = NewProcessDefinitionId("runtime-counted-loop");
        var suffix = Guid.NewGuid().ToString("N");
        var recipeId = $"recipe-process-runtime-loop-{suffix}";
        var stationProfileId = $"station-process-runtime-loop-{suffix}";
        var projectId = $"project-process-runtime-loop-{suffix}";
        var configurationSnapshotId = $"snapshot-process-runtime-loop-{suffix}";

        using var createResponse = await client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateCountedLoopDefinitionRequest(processDefinitionId));
        using var createBody = await ReadJsonAsync(createResponse);
        using var publishResponse = await client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            projectId,
            configurationSnapshotId,
            processDefinitionId,
            client);
        using var startResponse = await client.PostAsJsonAsync(
            $"/api/process-definitions/{processDefinitionId}/runtime-sessions",
            new
            {
                configurationSnapshotId
            });
        using var startBody = await ReadJsonAsync(startResponse);

        var loopTransition = createBody.RootElement
            .GetProperty("transitions")
            .EnumerateArray()
            .Single(transition => transition.GetProperty("transitionId").GetString() == "route-result-to-read-result-retry");

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal("Counted", loopTransition.GetProperty("loopPolicy").GetString());
        Assert.Equal(2, loopTransition.GetProperty("maxTraversals").GetInt32());
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, startBody.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(1, startBody.RootElement.GetProperty("commandCount").GetInt32());
    }

    [Fact]
    public async Task DeviceBackedRuntimeExecutorUsesEngineeringSnapshotDeviceBinding()
    {
        using var factory = DevelopmentRuntimeStartTestHost.Create(
            _factory,
            new Dictionary<string, string?>
            {
                ["OpenLineOps:Runtime:CommandExecutor"] = "Device",
                ["OpenLineOps:Devices:CommandRouting:Provider"] = "Engineering"
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var processDefinitionId = NewProcessDefinitionId("runtime-device-backed");
        var suffix = Guid.NewGuid().ToString("N");
        var recipeId = $"recipe-process-runtime-device-{suffix}";
        var stationProfileId = $"station-process-runtime-device-{suffix}";
        var projectId = $"project-process-runtime-device-{suffix}";
        var configurationSnapshotId = $"snapshot-process-runtime-device-{suffix}";

        using var createResponse = await client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));
        using var publishResponse = await client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            projectId,
            configurationSnapshotId,
            processDefinitionId,
            client);

        using var startResponse = await client.PostAsJsonAsync(
            $"/api/process-definitions/{processDefinitionId}/runtime-sessions",
            new
            {
                configurationSnapshotId
            });
        using var startBody = await ReadJsonAsync(startResponse);
        var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());

        using var queryResponse = await client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionBody = await ReadJsonAsync(queryResponse);
        var command = sessionBody.RootElement.GetProperty("commands")[0];

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal("device.scanner", command.GetProperty("targetCapability").GetString());
        Assert.Equal("Completed", command.GetProperty("status").GetString());
        var resultPayload = command.GetProperty("resultPayload").GetString();
        Assert.NotNull(resultPayload);
        Assert.Contains(
            "\"deviceInstanceId\":\"scanner-01\"",
            resultPayload,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DraftProcessDefinitionCannotStartRuntimeSession()
    {
        using var factory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var processDefinitionId = NewProcessDefinitionId("runtime-draft");
        var suffix = Guid.NewGuid().ToString("N");
        var recipeId = $"recipe-process-runtime-draft-{suffix}";
        var stationProfileId = $"station-process-runtime-draft-{suffix}";
        var projectId = $"project-process-runtime-draft-{suffix}";
        var configurationSnapshotId = $"snapshot-process-runtime-draft-{suffix}";

        using var createResponse = await client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            projectId,
            configurationSnapshotId,
            processDefinitionId,
            client);
        using var startResponse = await client.PostAsJsonAsync(
            $"/api/process-definitions/{processDefinitionId}/runtime-sessions",
            new
            {
                configurationSnapshotId
            });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, startResponse.StatusCode);
    }

    [Fact]
    public async Task PythonScriptProcessDefinitionCanStartRuntimeSessionAndReturnsScriptResult()
    {
        using var factory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var processDefinitionId = NewProcessDefinitionId("runtime-python-script");
        var suffix = Guid.NewGuid().ToString("N");
        var recipeId = $"recipe-process-runtime-python-{suffix}";
        var stationProfileId = $"station-process-runtime-python-{suffix}";
        var projectId = $"project-process-runtime-python-{suffix}";
        var configurationSnapshotId = $"snapshot-process-runtime-python-{suffix}";

        using var createResponse = await client.PostAsJsonAsync(
            "/api/process-definitions",
            CreatePythonScriptDefinitionRequest(processDefinitionId, scriptInputPayload: "scan-ok"));
        using var publishResponse = await client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            projectId,
            configurationSnapshotId,
            processDefinitionId,
            client);
        using var startResponse = await client.PostAsJsonAsync(
            $"/api/process-definitions/{processDefinitionId}/runtime-sessions",
            new
            {
                configurationSnapshotId
            });
        using var startBody = await ReadJsonAsync(startResponse);
        var sessionId = startBody.RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        Assert.Equal("Completed", startBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, startBody.RootElement.GetProperty("completedSteps").GetInt32());
        Assert.Equal(1, startBody.RootElement.GetProperty("commandCount").GetInt32());

        using var queryResponse = await client.GetAsync($"/api/runtime/sessions/{sessionId}");
        using var sessionBody = await ReadJsonAsync(queryResponse);
        var command = sessionBody.RootElement.GetProperty("commands")[0];

        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        Assert.Equal("process.python-script", command.GetProperty("targetCapability").GetString());
        Assert.Equal("PythonScript.Execute", command.GetProperty("commandName").GetString());
        Assert.Equal("Completed", command.GetProperty("status").GetString());
        Assert.Contains(
            "\"normalized\":\"scan-ok\"",
            command.GetProperty("resultPayload").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationSnapshotForDifferentProcessCannotStartRuntimeSession()
    {
        using var factory = DevelopmentRuntimeStartTestHost.Create(_factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var processDefinitionId = NewProcessDefinitionId("runtime-snapshot-mismatch");
        var otherProcessDefinitionId = NewProcessDefinitionId("runtime-snapshot-other");
        var suffix = Guid.NewGuid().ToString("N");
        var recipeId = $"recipe-process-runtime-mismatch-{suffix}";
        var stationProfileId = $"station-process-runtime-mismatch-{suffix}";
        var projectId = $"project-process-runtime-mismatch-{suffix}";
        var configurationSnapshotId = $"snapshot-process-runtime-mismatch-{suffix}";

        using var createResponse = await client.PostAsJsonAsync(
            "/api/process-definitions",
            CreateValidDefinitionRequest(processDefinitionId));
        using var publishResponse = await client.PostAsync($"/api/process-definitions/{processDefinitionId}/publish", content: null);
        await CreatePublishedEngineeringSnapshotAsync(
            recipeId,
            stationProfileId,
            projectId,
            configurationSnapshotId,
            otherProcessDefinitionId,
            client);

        using var startResponse = await client.PostAsJsonAsync(
            $"/api/process-definitions/{processDefinitionId}/runtime-sessions",
            new
            {
                configurationSnapshotId
            });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, startResponse.StatusCode);
    }

    private static object CreateValidDefinitionRequest(string processDefinitionId)
    {
        return new
        {
            processDefinitionId,
            versionId = "packaging-line-eol@1.0.0",
            displayName = "Packaging Line End Of Line Test",
            nodes = new[]
            {
                new
                {
                    nodeId = "start",
                    kind = "Start",
                    displayName = "Start",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                },
                new
                {
                    nodeId = "inspect",
                    kind = "Command",
                    displayName = "Inspect",
                    requiredCapability = (string?)"device.scanner",
                    commandName = (string?)"Inspect",
                    timeoutSeconds = (int?)30,
                    inputPayload = (string?)"scan-ok"
                },
                new
                {
                    nodeId = "end",
                    kind = "End",
                    displayName = "End",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "start-to-inspect",
                    fromNodeId = "start",
                    toNodeId = "inspect",
                    label = (string?)null
                },
                new
                {
                    transitionId = "inspect-to-end",
                    fromNodeId = "inspect",
                    toNodeId = "end",
                    label = (string?)"ok"
                }
            }
        };
    }

    private static object CreateBranchingDefinitionRequest(string processDefinitionId)
    {
        return new
        {
            processDefinitionId,
            versionId = "packaging-line-eol@1.0.0",
            displayName = "Branching End Of Line Test",
            nodes = new[]
            {
                new
                {
                    nodeId = "start",
                    kind = "Start",
                    displayName = "Start",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                },
                new
                {
                    nodeId = "read-result",
                    kind = "Command",
                    displayName = "Read Result",
                    requiredCapability = (string?)"device.scanner",
                    commandName = (string?)"ReadResult",
                    timeoutSeconds = (int?)30,
                    inputPayload = (string?)"""{"status":"ok"}"""
                },
                new
                {
                    nodeId = "route-result",
                    kind = "Decision",
                    displayName = "Route Result",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                },
                new
                {
                    nodeId = "open-pass-gate",
                    kind = "Command",
                    displayName = "Open Pass Gate",
                    requiredCapability = (string?)"device.io",
                    commandName = (string?)"OpenPassGate",
                    timeoutSeconds = (int?)30,
                    inputPayload = (string?)"pass"
                },
                new
                {
                    nodeId = "open-fail-gate",
                    kind = "Command",
                    displayName = "Open Fail Gate",
                    requiredCapability = (string?)"device.io",
                    commandName = (string?)"OpenFailGate",
                    timeoutSeconds = (int?)30,
                    inputPayload = (string?)"fail"
                },
                new
                {
                    nodeId = "end",
                    kind = "End",
                    displayName = "End",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "start-to-read-result",
                    fromNodeId = "start",
                    toNodeId = "read-result",
                    label = (string?)null
                },
                new
                {
                    transitionId = "read-result-to-route-result",
                    fromNodeId = "read-result",
                    toNodeId = "route-result",
                    label = (string?)null
                },
                new
                {
                    transitionId = "route-result-to-open-pass-gate",
                    fromNodeId = "route-result",
                    toNodeId = "open-pass-gate",
                    label = (string?)"ok"
                },
                new
                {
                    transitionId = "route-result-to-open-fail-gate",
                    fromNodeId = "route-result",
                    toNodeId = "open-fail-gate",
                    label = (string?)"ng"
                },
                new
                {
                    transitionId = "open-pass-gate-to-end",
                    fromNodeId = "open-pass-gate",
                    toNodeId = "end",
                    label = (string?)null
                },
                new
                {
                    transitionId = "open-fail-gate-to-end",
                    fromNodeId = "open-fail-gate",
                    toNodeId = "end",
                    label = (string?)null
                }
            }
        };
    }

    private static object CreateCountedLoopDefinitionRequest(string processDefinitionId)
    {
        return new
        {
            processDefinitionId,
            versionId = "packaging-line-eol@1.0.0",
            displayName = "Counted Loop End Of Line Test",
            nodes = new[]
            {
                new
                {
                    nodeId = "start",
                    kind = "Start",
                    displayName = "Start",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                },
                new
                {
                    nodeId = "read-result",
                    kind = "Command",
                    displayName = "Read Result",
                    requiredCapability = (string?)"device.scanner",
                    commandName = (string?)"ReadResult",
                    timeoutSeconds = (int?)30,
                    inputPayload = (string?)"""{"status":"ok"}"""
                },
                new
                {
                    nodeId = "route-result",
                    kind = "Decision",
                    displayName = "Route Result",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                },
                new
                {
                    nodeId = "end",
                    kind = "End",
                    displayName = "End",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "start-to-read-result",
                    fromNodeId = "start",
                    toNodeId = "read-result",
                    label = (string?)null,
                    loopPolicy = (string?)null,
                    maxTraversals = (int?)null
                },
                new
                {
                    transitionId = "read-result-to-route-result",
                    fromNodeId = "read-result",
                    toNodeId = "route-result",
                    label = (string?)null,
                    loopPolicy = (string?)null,
                    maxTraversals = (int?)null
                },
                new
                {
                    transitionId = "route-result-to-read-result-retry",
                    fromNodeId = "route-result",
                    toNodeId = "read-result",
                    label = (string?)"retry",
                    loopPolicy = (string?)"Counted",
                    maxTraversals = (int?)2
                },
                new
                {
                    transitionId = "route-result-to-end-ok",
                    fromNodeId = "route-result",
                    toNodeId = "end",
                    label = (string?)"ok",
                    loopPolicy = (string?)null,
                    maxTraversals = (int?)null
                }
            }
        };
    }

    private static object CreatePythonScriptDefinitionRequest(
        string processDefinitionId,
        string? scriptEditorMode = null,
        string scriptSourceCode = "result = {'normalized': input_payload}",
        string? scriptInputPayload = null)
    {
        return new
        {
            processDefinitionId,
            versionId = "packaging-line-eol@1.0.0",
            displayName = "Python Script Process",
            nodes = new[]
            {
                new
                {
                    nodeId = "start",
                    kind = "Start",
                    displayName = "Start",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null,
                    scriptEditorMode = (string?)null,
                    blocklyWorkspaceJson = (string?)null,
                    scriptSourceCode = (string?)null,
                    scriptVersion = (string?)null
                },
                new
                {
                    nodeId = "normalize",
                    kind = "PythonScript",
                    displayName = "Normalize Measurement",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)15,
                    inputPayload = scriptInputPayload,
                    scriptEditorMode,
                    blocklyWorkspaceJson = (string?)"""{"blocks":{"languageVersion":0}}""",
                    scriptSourceCode = (string?)scriptSourceCode,
                    scriptVersion = (string?)"7"
                },
                new
                {
                    nodeId = "end",
                    kind = "End",
                    displayName = "End",
                    requiredCapability = (string?)null,
                    commandName = (string?)null,
                    timeoutSeconds = (int?)null,
                    inputPayload = (string?)null,
                    scriptEditorMode = (string?)null,
                    blocklyWorkspaceJson = (string?)null,
                    scriptSourceCode = (string?)null,
                    scriptVersion = (string?)null
                }
            },
            transitions = new[]
            {
                new
                {
                    transitionId = "start-to-normalize",
                    fromNodeId = "start",
                    toNodeId = "normalize",
                    label = (string?)null
                },
                new
                {
                    transitionId = "normalize-to-end",
                    fromNodeId = "normalize",
                    toNodeId = "end",
                    label = (string?)"done"
                }
            }
        };
    }

    private async Task CreatePublishedEngineeringSnapshotAsync(
        string recipeId,
        string stationProfileId,
        string projectId,
        string configurationSnapshotId,
        string processDefinitionId,
        HttpClient? client = null)
    {
        var httpClient = client ?? _client;
        var workspaceId = $"workspace-{projectId}";

        using var createRecipeResponse = await httpClient.PostAsJsonAsync(
            "/api/engineering/recipes",
            new
            {
                recipeId,
                versionId = $"{recipeId}@1.0.0",
                displayName = "End Of Line Recipe",
                parameters = new[]
                {
                    new
                    {
                        key = "voltage.max",
                        value = "5.2"
                    }
                }
            });
        using var publishRecipeResponse = await httpClient.PostAsync(
            $"/api/engineering/recipes/{recipeId}/publish",
            content: null);
        using var createStationResponse = await httpClient.PostAsJsonAsync(
            "/api/engineering/station-profiles",
            new
            {
                stationProfileId,
                displayName = "End Of Line Station",
                deviceBindings = new[]
                {
                    new
                    {
                        deviceBindingId = "scanner-primary",
                        capabilityId = "device.scanner",
                        deviceKey = "scanner-01"
                    }
                }
            });
        using var createWorkspaceResponse = await httpClient.PostAsJsonAsync(
            "/api/engineering/workspaces",
            new
            {
                workspaceId,
                displayName = "Main Workspace"
            });
        using var createProjectResponse = await httpClient.PostAsJsonAsync(
            "/api/engineering/projects",
            new
            {
                projectId,
                workspaceId,
                displayName = "Packaging Line Project"
            });
        using var publishSnapshotResponse = await httpClient.PostAsJsonAsync(
            $"/api/engineering/projects/{projectId}/configuration-snapshots",
            new
            {
                snapshotId = configurationSnapshotId,
                processDefinitionId,
                processVersionId = "packaging-line-eol@1.0.0",
                recipeId,
                stationProfileId
            });

        Assert.Equal(HttpStatusCode.Created, createRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, publishRecipeResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createStationResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createWorkspaceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createProjectResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, publishSnapshotResponse.StatusCode);
    }

    private static string NewProcessDefinitionId(string suffix)
    {
        return $"process-api-{suffix}-{Guid.NewGuid():N}";
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

}
