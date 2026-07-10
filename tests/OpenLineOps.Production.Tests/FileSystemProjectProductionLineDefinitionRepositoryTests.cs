using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;
using OpenLineOps.Production.Domain.Models;
using OpenLineOps.Production.Infrastructure.Persistence;

namespace OpenLineOps.Production.Tests;

public sealed class FileSystemProjectProductionLineDefinitionRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "openlineops-production-repository-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PortableApplicationFolderCanBeCopiedAndReadWithoutRewrite()
    {
        var sourceScope = Scope(Path.Combine(_root, "project-a"), "project.a");
        var targetScope = Scope(Path.Combine(_root, "project-b"), "project.b");
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        var definition = Definition();
        await repository.SaveAsync(sourceScope, definition);
        var sourcePath = Path.Combine(
            sourceScope.ApplicationRootPath,
            "production",
            "lines",
            "line.main",
            "line.json");
        Assert.True(File.Exists(sourcePath));
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var sourceJson = await File.ReadAllTextAsync(sourcePath);
        Assert.DoesNotContain("projectId", sourceJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceScope.ProjectId, sourceJson, StringComparison.Ordinal);

        CopyDirectory(sourceScope.ApplicationRootPath, targetScope.ApplicationRootPath);
        var targetPath = Path.Combine(
            targetScope.ApplicationRootPath,
            "production",
            "lines",
            "line.main",
            "line.json");
        var copiedTimestamp = new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(targetPath, copiedTimestamp);

        var restored = await repository.GetByIdAsync(
            targetScope,
            new ProductionLineDefinitionId("line.main"));

        Assert.NotNull(restored);
        Assert.Equal("MODEL-A", restored.DutModel.ModelCode);
        Assert.Equal(["stage.load", "stage.test"], restored.Stages.Select(stage => stage.Id.Value));
        await repository.SaveAsync(targetScope, restored);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(copiedTimestamp, File.GetLastWriteTimeUtc(targetPath));
    }

    [Fact]
    public async Task WriterRejectsLineIdsThatDifferOnlyByCase()
    {
        var scope = Scope(Path.Combine(_root, "case-conflict"));
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        await repository.SaveAsync(scope, Definition());
        var conflicting = Definition("LINE.main");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.SaveAsync(scope, conflicting));

        Assert.Contains("ignoring case", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StrictV1ReaderRejectsUnknownFields()
    {
        var scope = Scope(Path.Combine(_root, "strict"));
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        await repository.SaveAsync(scope, Definition());
        var path = Path.Combine(scope.ApplicationRootPath, "production", "lines", "line.main", "line.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["obsoleteField"] = true;
        await File.WriteAllTextAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, new ProductionLineDefinitionId("line.main")));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ProductionLineDefinition Definition(string lineDefinitionId = "line.main")
    {
        return ProductionLineDefinition.Create(
            new ProductionLineDefinitionId(lineDefinitionId),
            "Main Line",
            "topology.main",
            DutModelDefinition.Create(new DutModelId("dut.model-a"), "MODEL-A", "serialNumber"),
            [ProductionLineDefinitionDomainTests.Workstation()],
            [
                ProductionLineDefinitionDomainTests.Stage("stage.test", 2, "flow.test", "adapter.test"),
                ProductionLineDefinitionDomainTests.Stage("stage.load", 1, "flow.load")
            ],
            [ProductionLineDefinitionDomainTests.Adapter()],
            new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero));
    }

    private static ProjectApplicationWorkspaceScope Scope(
        string projectPath,
        string projectId = "host.project")
    {
        return new ProjectApplicationWorkspaceScope(
            projectId,
            "application.portable",
            projectPath,
            "applications/application.portable/application.portable.oloapp");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
