using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Infrastructure.ProjectWorkspaces;

namespace OpenLineOps.Projects.Tests;

public sealed class AutomationProjectFileFormatTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 7, 10, 6, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "openlineops-project-format-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveCreatesRootProjectAndOneApplicationProjectPerFolder()
    {
        var projectRoot = Path.Combine(_testRoot, "PackagingLine");
        var store = new FileSystemAutomationProjectManifestStore();
        var manifest = CreateManifest(projectRoot);

        await store.SaveAsync(manifest);

        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var applicationFilePath = Path.Combine(
            projectRoot,
            "applications",
            "main-line",
            "main-line.oloapp");
        Assert.True(File.Exists(projectFilePath));
        Assert.True(File.Exists(applicationFilePath));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "topology")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "layouts")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "flows")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "blocks", "custom")));
        Assert.True(Directory.Exists(Path.Combine(projectRoot, "applications", "main-line", "configuration")));

        using var projectDocument = JsonDocument.Parse(await File.ReadAllTextAsync(projectFilePath));
        Assert.Equal(
            AutomationProjectFile.CurrentSchemaVersion,
            projectDocument.RootElement.GetProperty("schemaVersion").GetString());
        Assert.False(projectDocument.RootElement.TryGetProperty("projectPath", out _));
        Assert.Equal(
            "applications/main-line/main-line.oloapp",
            projectDocument.RootElement
                .GetProperty("applications")[0]
                .GetProperty("projectFile")
                .GetString());

        using var applicationDocument = JsonDocument.Parse(await File.ReadAllTextAsync(applicationFilePath));
        Assert.Equal(
            AutomationApplicationProjectFile.CurrentSchemaVersion,
            applicationDocument.RootElement.GetProperty("schemaVersion").GetString());
        Assert.False(applicationDocument.RootElement.TryGetProperty("projectId", out _));
        Assert.Equal("main-line", applicationDocument.RootElement.GetProperty("applicationId").GetString());
    }

    [Fact]
    public async Task OpenByExplicitProjectFileAfterMovingDirectoryDerivesNewProjectRoot()
    {
        var originalRoot = Path.Combine(_testRoot, "original", "PackagingLine");
        var movedRoot = Path.Combine(_testRoot, "moved", "PackagingLine");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(originalRoot));
        Directory.CreateDirectory(Path.GetDirectoryName(movedRoot)!);
        Directory.Move(originalRoot, movedRoot);

        var loaded = await store.LoadAsync(Path.Combine(movedRoot, "packaging-line.oloproj"));

        Assert.NotNull(loaded);
        Assert.Equal(Path.GetFullPath(movedRoot), loaded.ProjectPath);
        Assert.Equal(
            "applications/main-line/main-line.oloapp",
            Assert.Single(loaded.Applications).ProjectFilePath);
    }

    [Fact]
    public async Task LoadRejectsExistingFileWithoutProjectExtension()
    {
        var projectRoot = Path.Combine(_testRoot, "wrong-extension");
        Directory.CreateDirectory(projectRoot);
        var wrongFilePath = Path.Combine(projectRoot, "openlineops.project.json");
        await File.WriteAllTextAsync(
            wrongFilePath,
            "{}");
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(wrongFilePath));

        Assert.Contains(".oloproj", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadDirectoryWithoutProjectFileReturnsMissing()
    {
        var projectRoot = Path.Combine(_testRoot, "missing");
        Directory.CreateDirectory(projectRoot);
        var store = new FileSystemAutomationProjectManifestStore();

        var loaded = await store.LoadAsync(projectRoot);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadRejectsProjectThroughReparsePointAncestor()
    {
        var realAncestor = Path.Combine(_testRoot, "real");
        var realRoot = Path.Combine(realAncestor, "nested", "PackagingLine");
        var linkedAncestor = Path.Combine(_testRoot, "linked");
        var linkedRoot = Path.Combine(linkedAncestor, "nested", "PackagingLine");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(realRoot));
        CreateDirectoryReparsePoint(linkedAncestor, realAncestor);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.LoadAsync(Path.Combine(linkedRoot, "packaging-line.oloproj")));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(realRoot, "packaging-line.oloproj")));
        }
        finally
        {
            Directory.Delete(linkedAncestor);
        }
    }

    [Fact]
    public async Task SaveRejectsProspectiveProjectBelowJunctionBeforeCreatingTargetContent()
    {
        var targetAncestor = Path.Combine(_testRoot, "outside-target");
        var linkedAncestor = Path.Combine(_testRoot, "linked-create");
        var targetSentinel = Path.Combine(targetAncestor, "must-remain.txt");
        Directory.CreateDirectory(targetAncestor);
        await File.WriteAllTextAsync(targetSentinel, "unchanged");
        CreateDirectoryReparsePoint(linkedAncestor, targetAncestor);
        var projectRoot = Path.Combine(linkedAncestor, "must-not-exist", "PackagingLine");
        var store = new FileSystemAutomationProjectManifestStore();

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.SaveAsync(CreateManifest(projectRoot)));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(targetAncestor, "must-not-exist")));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(targetSentinel));
        }
        finally
        {
            Directory.Delete(linkedAncestor);
        }
    }

    [Fact]
    public async Task SaveRejectsReparsePointProjectEntryBeforeCreatingApplications()
    {
        var projectRoot = Path.Combine(_testRoot, "root-entry-reparse");
        var targetRoot = Path.Combine(_testRoot, "root-entry-target");
        var targetSentinel = Path.Combine(targetRoot, "must-remain.txt");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllTextAsync(targetSentinel, "unchanged");
        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        CreateDirectoryReparsePoint(projectFilePath, targetRoot);
        var store = new FileSystemAutomationProjectManifestStore();

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.SaveAsync(CreateManifest(projectRoot)));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(projectRoot, "applications")));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(targetSentinel));
        }
        finally
        {
            Directory.Delete(projectFilePath);
        }
    }

    [Fact]
    public async Task SaveRejectsReparsePointApplicationEntryBeforeChangingRootManifest()
    {
        var projectRoot = Path.Combine(_testRoot, "application-entry-reparse");
        var targetRoot = Path.Combine(_testRoot, "application-entry-target");
        var targetSentinel = Path.Combine(targetRoot, "must-remain.txt");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(projectRoot));
        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var projectBytes = await File.ReadAllBytesAsync(projectFilePath);
        var applicationFilePath = Path.Combine(
            projectRoot,
            "applications",
            "main-line",
            "main-line.oloapp");
        File.Delete(applicationFilePath);
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllTextAsync(targetSentinel, "unchanged");
        CreateDirectoryReparsePoint(applicationFilePath, targetRoot);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.SaveAsync(CreateManifest(projectRoot) with
                {
                    DisplayName = "Must Not Commit"
                }));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(projectBytes, await File.ReadAllBytesAsync(projectFilePath));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(targetSentinel));
        }
        finally
        {
            Directory.Delete(applicationFilePath);
        }
    }

    [Fact]
    public async Task SaveRejectsDirectoryAtProjectFilePathBeforeCreatingApplications()
    {
        var projectRoot = Path.Combine(_testRoot, "root-entry-directory");
        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var sentinelPath = Path.Combine(projectFilePath, "must-remain.txt");
        Directory.CreateDirectory(projectFilePath);
        await File.WriteAllTextAsync(sentinelPath, "unchanged");
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.SaveAsync(CreateManifest(projectRoot)));

        Assert.Contains("ordinary file", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(projectRoot, "applications")));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinelPath));
    }

    [Fact]
    public async Task SaveRejectsDirectoryAtApplicationFilePathBeforeChangingRootManifest()
    {
        var projectRoot = Path.Combine(_testRoot, "application-entry-directory");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(projectRoot));
        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var projectBytes = await File.ReadAllBytesAsync(projectFilePath);
        var applicationFilePath = Path.Combine(
            projectRoot,
            "applications",
            "main-line",
            "main-line.oloapp");
        File.Delete(applicationFilePath);
        Directory.CreateDirectory(applicationFilePath);
        var sentinelPath = Path.Combine(applicationFilePath, "must-remain.txt");
        await File.WriteAllTextAsync(sentinelPath, "unchanged");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.SaveAsync(CreateManifest(projectRoot) with
            {
                DisplayName = "Must Not Commit"
            }));

        Assert.Contains("ordinary file", exception.Message, StringComparison.Ordinal);
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(projectFilePath));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinelPath));
    }

    [Fact]
    public async Task SavePreflightsEveryApplicationDirectoryBeforeCreatingAnyOfThem()
    {
        var projectRoot = Path.Combine(_testRoot, "application-directory-preflight");
        var applicationRoot = Path.Combine(projectRoot, "applications", "main-line");
        var layoutsPath = Path.Combine(applicationRoot, "layouts");
        var topologyPath = Path.Combine(applicationRoot, "topology");
        var targetRoot = Path.Combine(_testRoot, "application-directory-preflight-target");
        var sentinelPath = Path.Combine(targetRoot, "must-remain.txt");
        Directory.CreateDirectory(applicationRoot);
        Directory.CreateDirectory(targetRoot);
        await File.WriteAllTextAsync(sentinelPath, "unchanged");
        CreateDirectoryReparsePoint(layoutsPath, targetRoot);
        var store = new FileSystemAutomationProjectManifestStore();

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.SaveAsync(CreateManifest(projectRoot)));

            Assert.Contains("ordinary filesystem entry", exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(topologyPath));
            Assert.False(File.Exists(Path.Combine(projectRoot, "packaging-line.oloproj")));
            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinelPath));
        }
        finally
        {
            Directory.Delete(layoutsPath);
        }
    }

    [Fact]
    public async Task LoadRejectsDirectoryAtExplicitProjectFilePath()
    {
        var projectRoot = Path.Combine(_testRoot, "load-project-entry-directory");
        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        Directory.CreateDirectory(projectFilePath);
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectFilePath));

        Assert.Contains("ordinary file", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRejectsReparsePointProjectFile()
    {
        var sourceRoot = Path.Combine(_testRoot, "source", "PackagingLine");
        var linkedRoot = Path.Combine(_testRoot, "linked-file");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(sourceRoot));
        Directory.CreateDirectory(linkedRoot);
        var sourceProjectFile = Path.Combine(sourceRoot, "packaging-line.oloproj");
        var linkedProjectFile = Path.Combine(linkedRoot, "packaging-line.oloproj");
        try
        {
            File.CreateSymbolicLink(linkedProjectFile, sourceProjectFile);
        }
        catch (Exception creationException) when (creationException is UnauthorizedAccessException
                                                  or IOException
                                                  or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await store.LoadAsync(linkedProjectFile));

            Assert.Contains("reparse", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedProjectFile);
        }
    }

    [Fact]
    public async Task LoadRejectsUppercaseProjectExtension()
    {
        var projectRoot = Path.Combine(_testRoot, "uppercase-extension");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(projectRoot));
        var canonicalPath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var intermediatePath = Path.Combine(projectRoot, "packaging-line.rename");
        var uppercasePath = Path.Combine(projectRoot, "packaging-line.OLOPROJ");
        File.Move(canonicalPath, intermediatePath);
        File.Move(intermediatePath, uppercasePath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectRoot));

        Assert.Contains("canonical lowercase .oloproj", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadRejectsNonexistentUppercaseProjectTargetWithoutCreatingDirectory()
    {
        var uppercaseTarget = Path.Combine(_testRoot, "missing-line.OLOPROJ");
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(uppercaseTarget));

        Assert.Contains("canonical lowercase .oloproj", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(uppercaseTarget));
        Assert.False(File.Exists(uppercaseTarget));
    }

    [Fact]
    public void ApplicationProjectPathRejectsUppercaseExtension()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            AutomationProjectFileConvention.ValidateApplicationProjectRelativePath(
                "applications/main/main.OLOAPP"));

        Assert.Contains(".oloapp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectRelativePathResolutionPreservesTheFilesystemRoot()
    {
        var fileSystemRoot = Path.GetPathRoot(Path.GetFullPath(_testRoot))
            ?? throw new InvalidOperationException("Test path has no filesystem root.");

        var resolved = AutomationProjectFileConvention.ResolveApplicationProjectPath(
            fileSystemRoot,
            "applications/main/main.oloapp");

        Assert.Equal(
            Path.Combine(fileSystemRoot, "applications", "main", "main.oloapp"),
            resolved);
    }

    [Fact]
    public async Task LoadRejectsUnsupportedRootProjectFormat()
    {
        var projectRoot = Path.Combine(_testRoot, "unsupported-root");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(projectRoot));
        var projectFilePath = Path.Combine(projectRoot, "packaging-line.oloproj");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(projectFilePath))!.AsObject();
        document["formatVersion"] = 99;
        await File.WriteAllTextAsync(projectFilePath, document.ToJsonString(JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectFilePath));
    }

    [Fact]
    public async Task LoadRejectsRemovedHostProjectIdFieldInApplicationProject()
    {
        var projectRoot = Path.Combine(_testRoot, "strict-application");
        var store = new FileSystemAutomationProjectManifestStore();
        await store.SaveAsync(CreateManifest(projectRoot));
        var applicationFilePath = Path.Combine(
            projectRoot,
            "applications",
            "main-line",
            "main-line.oloapp");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(applicationFilePath))!.AsObject();
        document["projectId"] = "removed-host-project";
        await File.WriteAllTextAsync(applicationFilePath, document.ToJsonString(JsonOptions));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectRoot));
    }

    [Fact]
    public async Task LoadRejectsApplicationProjectPathTraversal()
    {
        var projectRoot = Path.Combine(_testRoot, "traversal");
        Directory.CreateDirectory(projectRoot);
        var projectFile = new AutomationProjectFile(
            AutomationProjectFile.CurrentSchemaVersion,
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectFile.KindName,
            AutomationProjectManifest.ProductName,
            "project.traversal",
            "Traversal",
            CreatedAtUtc,
            CreatedAtUtc,
            ActiveSnapshotId: null,
            [new AutomationProjectApplicationReference("application.main", "../escape/main.oloapp")],
            Snapshots: []);
        var projectFilePath = Path.Combine(projectRoot, "project.traversal.oloproj");
        await File.WriteAllTextAsync(
            projectFilePath,
            JsonSerializer.Serialize(projectFile, JsonOptions));
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectFilePath));

        Assert.Contains("../escape/main.oloapp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectoryWithMultipleProjectFilesIsRejectedAsAmbiguous()
    {
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(Path.Combine(_testRoot, "one.oloproj"), "{}");
        File.WriteAllText(Path.Combine(_testRoot, "two.oloproj"), "{}");
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = Assert.Throws<InvalidDataException>(() => store.GetManifestPath(_testRoot));

        Assert.Contains("multiple", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("releaseManifestPath", true)]
    [InlineData("releaseContentSha256", true)]
    [InlineData("releaseManifestPath", false)]
    [InlineData("releaseContentSha256", false)]
    public async Task LoadRejectsSnapshotWithoutCompleteImmutableReleaseDescriptor(
        string fieldName,
        bool removeField)
    {
        var projectRoot = Path.Combine(_testRoot, $"snapshot-{fieldName}-{removeField}");
        Directory.CreateDirectory(projectRoot);
        var projectFilePath = Path.Combine(projectRoot, "project.oloproj");
        var snapshot = new AutomationProjectSnapshotFile(
            "snapshot.main",
            "application.main",
            "topology.main",
            ["layout.main"],
            "line.main",
            CreatedAtUtc,
            [],
            [],
            [],
            "releases/release-main/release.json",
            new string('a', 64));
        var projectFile = new AutomationProjectFile(
            AutomationProjectFile.CurrentSchemaVersion,
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectFile.KindName,
            AutomationProjectManifest.ProductName,
            "project.main",
            "Project Main",
            CreatedAtUtc,
            CreatedAtUtc,
            null,
            [],
            [snapshot]);
        var document = JsonNode.Parse(JsonSerializer.Serialize(projectFile, JsonOptions))!;
        var snapshotNode = document["snapshots"]![0]!.AsObject();
        if (removeField)
        {
            snapshotNode.Remove(fieldName);
        }
        else
        {
            snapshotNode[fieldName] = string.Empty;
        }

        await File.WriteAllTextAsync(projectFilePath, document.ToJsonString(JsonOptions));
        var store = new FileSystemAutomationProjectManifestStore();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(projectFilePath));

        Assert.Contains(fieldName, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static void CreateDirectoryReparsePoint(string path, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, targetPath);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "/d",
                "/c",
                "mklink",
                "/J",
                path,
                targetPath
            }
        }) ?? throw new InvalidOperationException("Failed to start the Windows junction command.");

        process.WaitForExit();
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        Assert.True(
            process.ExitCode == 0,
            $"Failed to create test junction. stdout: {standardOutput} stderr: {standardError}");
    }

    private static AutomationProjectManifest CreateManifest(string projectRoot)
    {
        return new AutomationProjectManifest(
            AutomationProjectManifest.CurrentFormatVersion,
            AutomationProjectManifest.ProductName,
            "packaging-line",
            "Packaging Line",
            projectRoot,
            CreatedAtUtc,
            CreatedAtUtc,
            ActiveSnapshotId: null,
            [
                new ProjectApplicationManifest(
                    "main-line",
                    "Main Line",
                    TopologyId: null,
                    ProcessDefinitionIds: [],
                    "applications/main-line/main-line.oloapp",
                    PluginPackageReferences: [])
            ],
            Snapshots: []);
    }
}
