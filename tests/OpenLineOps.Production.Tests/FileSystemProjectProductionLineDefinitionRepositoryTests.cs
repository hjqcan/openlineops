using System.Text.Json;
using System.Text.Json.Nodes;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
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
        var definition = ProductionLineDefinitionDomainTests.Definition();
        await repository.SaveAsync(sourceScope, definition);
        var sourcePath = LinePath(sourceScope);
        Assert.True(File.Exists(sourcePath));
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath);
        var sourceJson = await File.ReadAllTextAsync(sourcePath);
        Assert.DoesNotContain("projectId", sourceJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceScope.ProjectId, sourceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"dutModel\"", sourceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"workstations\"", sourceJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stages\"", sourceJson, StringComparison.Ordinal);
        Assert.Contains("\"productModel\"", sourceJson, StringComparison.Ordinal);
        Assert.Contains("\"operations\"", sourceJson, StringComparison.Ordinal);
        Assert.Contains("\"transitions\"", sourceJson, StringComparison.Ordinal);

        CopyDirectory(sourceScope.ApplicationRootPath, targetScope.ApplicationRootPath);
        var targetPath = LinePath(targetScope);
        var copiedTimestamp = new DateTime(2026, 7, 10, 1, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(targetPath, copiedTimestamp);

        var restored = await repository.GetByIdAsync(
            targetScope,
            new ProductionLineDefinitionId("line.main"));

        Assert.NotNull(restored);
        Assert.Equal("MODEL-A", restored.ProductModel.ModelCode);
        Assert.Equal("operation.load", restored.EntryOperationId.Value);
        Assert.Equal(
            ["operation.load", "operation.test"],
            restored.Operations.Select(operation => operation.Id.Value));
        Assert.Equal(RouteTransitionKind.Sequence.ToString(), restored.Transitions.Single().Kind.ToString());
        await repository.SaveAsync(targetScope, restored);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(targetPath));
        Assert.Equal(copiedTimestamp, File.GetLastWriteTimeUtc(targetPath));
    }

    [Fact]
    public async Task WriterRejectsLineIdsThatDifferOnlyByCase()
    {
        var scope = Scope(Path.Combine(_root, "case-conflict"));
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        await repository.SaveAsync(scope, ProductionLineDefinitionDomainTests.Definition());
        var conflicting = ProductionLineDefinitionDomainTests.Definition("LINE.main");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.SaveAsync(scope, conflicting));

        Assert.Contains("ignoring case", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StrictReaderRejectsUnknownFields()
    {
        var scope = Scope(Path.Combine(_root, "strict"));
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        await repository.SaveAsync(scope, ProductionLineDefinitionDomainTests.Definition());
        var path = LinePath(scope);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["unknownField"] = true;
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, new ProductionLineDefinitionId("line.main")));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StrictReaderRejectsFormerStageShape()
    {
        var scope = Scope(Path.Combine(_root, "former-shape"));
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        await repository.SaveAsync(scope, ProductionLineDefinitionDomainTests.Definition());
        var path = LinePath(scope);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["stages"] = new JsonArray();
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, new ProductionLineDefinitionId("line.main")));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StrictReaderRejectsNonCanonicalTransitionToken()
    {
        var scope = Scope(Path.Combine(_root, "transition-token"));
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        await repository.SaveAsync(scope, ProductionLineDefinitionDomainTests.Definition());
        var path = LinePath(scope);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["transitions"]![0]!["kind"] = "sequence";
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, new ProductionLineDefinitionId("line.main")));

        Assert.Contains("route transition kind", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string LinePath(ProjectApplicationWorkspaceScope scope) => Path.Combine(
        scope.ApplicationRootPath,
        "production",
        "lines",
        "line.main",
        "line.json");

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
