using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.ExternalPrograms;
using OpenLineOps.Projects.Infrastructure.ExternalPrograms;
using OpenLineOps.Runtime.Contracts;

namespace OpenLineOps.Projects.Tests;

public sealed class ExternalProgramResourceRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset UpdatedAtUtc =
        new(2026, 7, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "openlineops-external-program-resources",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveFreezesUploadedBytesWithStrictHashInventory()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var bytes = Encoding.UTF8.GetBytes("vendor-helper-binary");
        var resource = await repository.SaveAsync(
            scope,
            ExecutableRequest("Vendor Helper"),
            [Upload("files/vendor-helper.exe", bytes)],
            UpdatedAtUtc);

        Assert.Equal("program.vendor-helper", resource.ResourceId);
        Assert.Equal(Sha256(bytes), Assert.Single(resource.Files).Sha256);
        Assert.True(ExternalProgramResourceContract.IsSha256(resource.ContentSha256));
        Assert.True(File.Exists(Path.Combine(
            scope.ApplicationRootPath,
            "external-programs",
            "program.vendor-helper",
            "resource.json")));

        var reopened = await repository.GetAsync(scope, resource.ResourceId);
        Assert.NotNull(reopened);
        Assert.Equal(resource.ContentSha256, reopened.ContentSha256);
        Assert.Equal(resource.DisplayName, reopened.DisplayName);
        Assert.Equal(resource.Files.ToArray(), reopened.Files.ToArray());
    }

    [Fact]
    public async Task ReadRejectsPayloadTamperingAfterDescriptorCommit()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var bytes = Encoding.UTF8.GetBytes("trusted");
        await repository.SaveAsync(
            scope,
            ExecutableRequest("Trusted"),
            [Upload("files/vendor-helper.exe", bytes)],
            UpdatedAtUtc);
        await File.WriteAllTextAsync(
            Path.Combine(
                scope.ApplicationRootPath,
                "external-programs",
                "program.vendor-helper",
                "files",
                "vendor-helper.exe"),
            "tampered");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetAsync(scope, "program.vendor-helper"));

        Assert.Contains("inventory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadRejectsDeclaredHashMismatchWithoutPublishingPartialResource()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var bytes = Encoding.UTF8.GetBytes("actual");
        var upload = new ExternalProgramFileUpload(
            "files/vendor-helper.exe",
            new MemoryStream(bytes, writable: false),
            bytes.Length,
            new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.SaveAsync(
                scope,
                ExecutableRequest("Hash mismatch"),
                [upload],
                UpdatedAtUtc));

        Assert.False(Directory.Exists(Path.Combine(
            scope.ApplicationRootPath,
            "external-programs",
            "program.vendor-helper")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(scope.ApplicationRootPath, "external-programs")));
    }

    [Fact]
    public async Task ConcurrentSaveAndGetNeverExposeHalfDescriptorOrFileInventory()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var first = Encoding.UTF8.GetBytes("first");
        await repository.SaveAsync(
            scope,
            ExecutableRequest("Initial"),
            [Upload("files/vendor-helper.exe", first)],
            UpdatedAtUtc);

        var second = Encoding.UTF8.GetBytes("second");
        var save = repository.SaveAsync(
            scope,
            ExecutableRequest("Replacement"),
            [Upload("files/vendor-helper.exe", second)],
            UpdatedAtUtc.AddMinutes(1)).AsTask();
        var readers = Enumerable.Range(0, 16)
            .Select(_ => repository.GetAsync(scope, "program.vendor-helper").AsTask())
            .ToArray();
        await Task.WhenAll(readers.Cast<Task>().Append(save));

        Assert.All(readers, reader => Assert.NotNull(reader.Result));
        var final = await repository.GetAsync(scope, "program.vendor-helper");
        Assert.Equal("Replacement", final!.DisplayName);
        Assert.Equal(Sha256(second), Assert.Single(final.Files).Sha256);
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("files/../escape.exe")]
    [InlineData("files\\escape.exe")]
    [InlineData("C:/escape.exe")]
    public async Task SaveRejectsNonCanonicalUploadPath(string path)
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var bytes = Encoding.UTF8.GetBytes("payload");

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await repository.SaveAsync(
                scope,
                ExecutableRequest("Invalid path"),
                [Upload(path, bytes)],
                UpdatedAtUtc));
    }

    [Fact]
    public async Task DeleteCommitsByDirectoryRenameAndRemovesResource()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var bytes = Encoding.UTF8.GetBytes("delete-me");
        await repository.SaveAsync(
            scope,
            ExecutableRequest("Delete"),
            [Upload("files/vendor-helper.exe", bytes)],
            UpdatedAtUtc);

        await repository.DeleteAsync(scope, "program.vendor-helper");

        Assert.Null(await repository.GetAsync(scope, "program.vendor-helper"));
        Assert.Empty(await repository.ListAsync(scope));
    }

    [Fact]
    public void DefinitionRejectsUnboundedLimits()
    {
        var request = ExecutableRequest("Unsafe") with
        {
            PermissionProfile = new ExternalProgramPermissionProfile(
                "Restricted",
                NetworkAccessAllowed: true,
                []),
            ExecutionLimits = ExecutableRequest("Unsafe").ExecutionLimits with
            {
                MaximumWorkingSetBytes = long.MaxValue
            }
        };

        Assert.Throws<ArgumentException>(() => ExternalProgramResourceValidator.ValidateDefinition(request));
    }

    [Fact]
    public void DefinitionAllowsExplicitRestrictedInternetClientPermission()
    {
        var request = ExecutableRequest("Networked") with
        {
            PermissionProfile = new ExternalProgramPermissionProfile(
                "Restricted",
                NetworkAccessAllowed: true,
                [])
        };

        ExternalProgramResourceValidator.ValidateDefinition(request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("echo-ok")]
    [InlineData("{not-json")]
    [InlineData("[]")]
    [InlineData("{\"axis\":\"x\",\"distance\":10}")]
    public void ReadReferenceTreatsOpaqueDeviceCommandPayloadAsOrdinaryCommand(string? inputPayload)
    {
        var reference = ExternalProgramResourceContract.ReadReference(inputPayload);

        Assert.Null(reference.ResourceId);
        Assert.False(reference.IsMalformed);
    }

    [Theory]
    [InlineData("{\"externalProgramResourceId\":null}")]
    [InlineData("{\"externalProgramResourceId\":\"\"}")]
    [InlineData("{\"externalProgramResourceId\":\"../escape\"}")]
    [InlineData("{\"externalProgramResourceId\":\"program.vendor-helper\",\"externalProgramResourceId\":\"program.other\"}")]
    public void ReadReferenceRejectsMalformedExplicitExternalProgramReference(string inputPayload)
    {
        var reference = ExternalProgramResourceContract.ReadReference(inputPayload);

        Assert.Null(reference.ResourceId);
        Assert.True(reference.IsMalformed);
    }

    [Fact]
    public void ReadReferenceReturnsCanonicalExplicitExternalProgramResourceId()
    {
        var reference = ExternalProgramResourceContract.ReadReference(
            "{\"externalProgramResourceId\":\"program.vendor-helper\",\"serial\":\"SN-1\"}");

        Assert.Equal("program.vendor-helper", reference.ResourceId);
        Assert.False(reference.IsMalformed);
    }

    private ProjectApplicationWorkspaceScope CreateScope()
    {
        var scope = new ProjectApplicationWorkspaceScope(
            "project.main",
            "application.main",
            _root,
            "applications/application.main/application.main.oloapp");
        Directory.CreateDirectory(scope.ApplicationRootPath);
        File.WriteAllText(scope.ApplicationProjectFilePath, "{\"applicationId\":\"application.main\"}");
        return scope;
    }

    private static SaveExternalProgramResourceRequest ExecutableRequest(string displayName) => new(
        "program.vendor-helper",
        displayName,
        "application.external-program",
        "Run",
        ExternalProgramLaunchKind.ApplicationExecutable,
        "files/vendor-helper.exe",
        ProviderKind: null,
        ProviderKey: null,
        ["--serial", "{{input.serial}}"],
        [
            new ExternalProgramInputMapping("$product.identity", "serial"),
            new ExternalProgramInputMapping("$product.model", "model")
        ],
        [new ExternalProgramResultMapping("$.outcome", "test.outcome", ProductionContextValueKind.Text)],
        new ExternalProgramOutcomeMapping("$.outcome", "Passed", "Failed", "Aborted"),
        new ExternalProgramPermissionProfile("Restricted", NetworkAccessAllowed: false, ["SystemRoot"]),
        new ExternalProgramExecutionLimits(
            TimeoutMilliseconds: 30_000,
            MaximumProcessCount: 4,
            MaximumWorkingSetBytes: 256 * 1024 * 1024,
            MaximumCpuTimeMilliseconds: 30_000,
            MaximumStandardOutputBytes: 1024 * 1024,
            MaximumStandardErrorBytes: 1024 * 1024,
            MaximumArtifactCount: 32,
            MaximumArtifactBytes: 16 * 1024 * 1024,
            MaximumTotalArtifactBytes: 64 * 1024 * 1024));

    private static ExternalProgramFileUpload Upload(string path, byte[] bytes) => new(
        path,
        new MemoryStream(bytes, writable: false),
        bytes.Length,
        Sha256(bytes));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
