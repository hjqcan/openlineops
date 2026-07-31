using System.Diagnostics;
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
        Assert.Contains("\"routeLayout\"", sourceJson, StringComparison.Ordinal);

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
        Assert.Equal(
            [("operation.load", 120, 80), ("operation.test", 400, 80)],
            restored.RouteLayout.OperationPositions.Select(position => (
                position.OperationId.Value,
                position.X,
                position.Y)));
        var operationTransition = Assert.Single(restored.Transitions, transition =>
            transition.TargetOperationId is not null);
        Assert.Equal(RouteTransitionKind.Sequence, operationTransition.Kind);
        Assert.Equal(
            TerminalDisposition.Completed,
            Assert.Single(restored.Transitions, transition =>
                transition.TerminalDisposition is not null).TerminalDisposition);
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
    public async Task StrictReaderRejectsMissingRouteLayout()
    {
        var scope = Scope(Path.Combine(_root, "missing-route-layout"));
        var repository = new FileSystemProjectProductionLineDefinitionRepository();
        await repository.SaveAsync(scope, ProductionLineDefinitionDomainTests.Definition());
        var path = LinePath(scope);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document.Remove("routeLayout");
        await File.WriteAllTextAsync(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetByIdAsync(scope, new ProductionLineDefinitionId("line.main")));

        Assert.Contains("required semantic collections", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData("production")]
    [InlineData("lines")]
    public async Task SaveRejectsResourceDirectoryReparsePointWithoutWritingOutsideApplication(
        string resourceDirectoryName)
    {
        var scope = Scope(Path.Combine(_root, $"reparse-{resourceDirectoryName}"));
        Directory.CreateDirectory(scope.ApplicationRootPath);
        var productionDirectory = Path.Combine(scope.ApplicationRootPath, "production");
        if (resourceDirectoryName == "lines")
        {
            Directory.CreateDirectory(productionDirectory);
        }

        var outsideDirectory = Path.Combine(_root, $"outside-{resourceDirectoryName}");
        Directory.CreateDirectory(outsideDirectory);
        var sentinelPath = Path.Combine(outsideDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "unchanged");
        var resourceDirectory = resourceDirectoryName == "production"
            ? productionDirectory
            : Path.Combine(productionDirectory, "lines");
        CreateDirectoryReparsePoint(resourceDirectory, outsideDirectory);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await new FileSystemProjectProductionLineDefinitionRepository()
                    .SaveAsync(scope, ProductionLineDefinitionDomainTests.Definition()));

            Assert.Equal("unchanged", await File.ReadAllTextAsync(sentinelPath));
            Assert.Equal([sentinelPath], Directory.GetFiles(outsideDirectory));
        }
        finally
        {
            Directory.Delete(resourceDirectory);
        }
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
}
