using System.Diagnostics;
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
        var resource = await repository.ImportDirectoryAsync(
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
        await repository.ImportDirectoryAsync(
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
            await repository.ImportDirectoryAsync(
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
    public async Task FailedDirectoryReplacementPreservesExistingDescriptorAndCompleteFileSet()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var originalExecutable = Encoding.UTF8.GetBytes("original-executable");
        var originalSupport = Encoding.UTF8.GetBytes("original-support");
        var original = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Original"),
            [
                Upload("files/vendor-helper.exe", originalExecutable),
                Upload("files/lib/original.dll", originalSupport)
            ],
            UpdatedAtUtc);

        var validReplacement = Encoding.UTF8.GetBytes("replacement-executable");
        var invalidContent = Encoding.UTF8.GetBytes("invalid-support");
        var failures = new ExternalProgramFileUpload[]
        {
            new(
                "files/lib/hash-mismatch.dll",
                new MemoryStream(invalidContent, writable: false),
                invalidContent.Length,
                new string('0', 64)),
            new(
                "files/lib/short.dll",
                new MemoryStream(invalidContent, writable: false),
                invalidContent.Length + 1,
                Sha256(invalidContent)),
            new(
                "files/lib/long.dll",
                new MemoryStream(invalidContent, writable: false),
                invalidContent.Length - 1,
                Sha256(invalidContent))
        };

        foreach (var failure in failures)
        {
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await repository.ImportDirectoryAsync(
                    scope,
                    ExecutableRequest("Must not commit"),
                    [Upload("files/vendor-helper.exe", validReplacement), failure],
                    UpdatedAtUtc.AddMinutes(1)));

            var reopened = await repository.GetAsync(scope, original.ResourceId);
            Assert.NotNull(reopened);
            Assert.Equal(original.ContentSha256, reopened.ContentSha256);
            Assert.Equal(original.Files.ToArray(), reopened.Files.ToArray());
            Assert.Equal(originalExecutable, await File.ReadAllBytesAsync(ResourceFilePath(
                scope,
                "files/vendor-helper.exe")));
            Assert.Equal(originalSupport, await File.ReadAllBytesAsync(ResourceFilePath(
                scope,
                "files/lib/original.dll")));
        }
    }

    [Fact]
    public async Task DirectoryReplacementAtomicallyRemovesStaleFilesAndPreservesNestedRelativePaths()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Original"),
            [
                Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("old")),
                Upload("files/lib/obsolete.dll", Encoding.UTF8.GetBytes("obsolete"))
            ],
            UpdatedAtUtc);

        var replacement = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Replacement") with { EntryPoint = "files/bin/vendor-helper.exe" },
            [
                Upload("files/bin/vendor-helper.exe", Encoding.UTF8.GetBytes("new")),
                Upload("files/config/shared.settings.json", Encoding.UTF8.GetBytes("config")),
                Upload("files/lib/shared.settings.json", Encoding.UTF8.GetBytes("library"))
            ],
            UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(
            [
                "files/bin/vendor-helper.exe",
                "files/config/shared.settings.json",
                "files/lib/shared.settings.json"
            ],
            replacement.Files.Select(file => file.RelativePath).ToArray());
        Assert.False(File.Exists(ResourceFilePath(scope, "files/vendor-helper.exe")));
        Assert.False(File.Exists(ResourceFilePath(scope, "files/lib/obsolete.dll")));
        Assert.Equal("config", await File.ReadAllTextAsync(ResourceFilePath(
            scope,
            "files/config/shared.settings.json")));
        Assert.Equal("library", await File.ReadAllTextAsync(ResourceFilePath(
            scope,
            "files/lib/shared.settings.json")));
    }

    [Fact]
    public async Task SwitchingExecutableResourceToProviderAtomicallyRemovesFrozenProgramFiles()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var executable = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Executable"),
            [
                Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("helper")),
                Upload("files/lib/support.dll", Encoding.UTF8.GetBytes("support"))
            ],
            UpdatedAtUtc);

        var provider = await repository.SaveDefinitionAsync(
            scope,
            ProviderRequest("Provider"),
            UpdatedAtUtc.AddMinutes(1));

        Assert.Empty(provider.Files);
        Assert.NotEqual(executable.ContentSha256, provider.ContentSha256);
        Assert.False(Directory.Exists(Path.GetDirectoryName(ResourceFilePath(
            scope,
            "files/vendor-helper.exe"))));
        var reopened = await repository.GetAsync(scope, provider.ResourceId);
        Assert.NotNull(reopened);
        Assert.Empty(reopened.Files);
        Assert.Equal(ExternalProgramLaunchKind.Provider, reopened.LaunchKind);
    }

    [Fact]
    public async Task InvalidProviderTransitionLeavesExecutableResourceAndFilesUntouched()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var executable = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Executable"),
            [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("helper"))],
            UpdatedAtUtc);
        var invalidProvider = ProviderRequest("Invalid Provider") with { ProviderKey = null };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.SaveDefinitionAsync(
                scope,
                invalidProvider,
                UpdatedAtUtc.AddMinutes(1)));

        var reopened = await repository.GetAsync(scope, executable.ResourceId);
        Assert.NotNull(reopened);
        Assert.Equal(executable.ContentSha256, reopened.ContentSha256);
        Assert.True(File.Exists(ResourceFilePath(scope, "files/vendor-helper.exe")));
    }

    [Fact]
    public async Task DirectoryImportRejectsPortableCaseCollision()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.ImportDirectoryAsync(
                scope,
                ExecutableRequest("Collision"),
                [
                    Upload("files/lib/Shared.dll", Encoding.UTF8.GetBytes("first")),
                    Upload("files/lib/shared.dll", Encoding.UTF8.GetBytes("second"))
                ],
                UpdatedAtUtc));
    }

    [Fact]
    public async Task ConcurrentSaveAndGetNeverExposeHalfDescriptorOrFileInventory()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var first = Encoding.UTF8.GetBytes("first");
        await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Initial"),
            [Upload("files/vendor-helper.exe", first)],
            UpdatedAtUtc);

        var second = Encoding.UTF8.GetBytes("second");
        var save = repository.ImportDirectoryAsync(
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

    [Fact]
    public async Task ListRecoversCrashGapAndRemovesDeterministicStagingDirectory()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var saved = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Recoverable"),
            [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("recover"))],
            UpdatedAtUtc);
        var resourcesRoot = Path.Combine(scope.ApplicationRootPath, "external-programs");
        var finalDirectory = Path.Combine(resourcesRoot, saved.ResourceId);
        var backupDirectory = Path.Combine(resourcesRoot, $".{saved.ResourceId}.backup");
        var stagingDirectory = Path.Combine(resourcesRoot, $".{saved.ResourceId}.staging");
        Directory.Move(finalDirectory, backupDirectory);
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "files"));
        await File.WriteAllTextAsync(
            Path.Combine(stagingDirectory, "files", "partial.tmp"),
            "partial");

        var recovered = Assert.Single(await repository.ListAsync(scope));

        Assert.Equal(saved.ContentSha256, recovered.ContentSha256);
        Assert.True(Directory.Exists(finalDirectory));
        Assert.False(Directory.Exists(backupDirectory));
        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Fact]
    public async Task UnexpectedResourceRootEntryBlocksReadAndReplacementWithoutMovingOldResource()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var original = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Original"),
            [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("original"))],
            UpdatedAtUtc);
        var unexpectedPath = Path.Combine(
            scope.ApplicationRootPath,
            "external-programs",
            original.ResourceId,
            "unexpected.tmp");
        await File.WriteAllTextAsync(unexpectedPath, "untrusted");

        var readFailure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetAsync(scope, original.ResourceId));
        Assert.Contains("not allowed", readFailure.Message, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ImportDirectoryAsync(
                scope,
                ExecutableRequest("Replacement"),
                [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("replacement"))],
                UpdatedAtUtc.AddMinutes(1)));

        Assert.Equal("original", await File.ReadAllTextAsync(ResourceFilePath(
            scope,
            "files/vendor-helper.exe")));
        File.Delete(unexpectedPath);
        var reopened = await repository.GetAsync(scope, original.ResourceId);
        Assert.NotNull(reopened);
        Assert.Equal(original.ContentSha256, reopened.ContentSha256);
    }

    [Fact]
    public async Task ResourceRootJunctionIsRejectedWithoutTouchingItsTargetOrReplacingTheResource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var original = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Original"),
            [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("original"))],
            UpdatedAtUtc);
        var outsideDirectory = Path.Combine(scope.ApplicationRootPath, "outside-resource");
        Directory.CreateDirectory(outsideDirectory);
        var sentinelPath = Path.Combine(outsideDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "must-survive");
        var junctionPath = Path.Combine(
            scope.ApplicationRootPath,
            "external-programs",
            original.ResourceId,
            "evil");
        await CreateJunctionAsync(junctionPath, outsideDirectory);

        try
        {
            var readFailure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await repository.GetAsync(scope, original.ResourceId));
            Assert.Contains("reparse", readFailure.Message, StringComparison.OrdinalIgnoreCase);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await repository.ImportDirectoryAsync(
                    scope,
                    ExecutableRequest("Replacement"),
                    [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("replacement"))],
                    UpdatedAtUtc.AddMinutes(1)));

            Assert.Equal("must-survive", await File.ReadAllTextAsync(sentinelPath));
            Assert.Equal("original", await File.ReadAllTextAsync(ResourceFilePath(
                scope,
                "files/vendor-helper.exe")));
        }
        finally
        {
            Directory.Delete(junctionPath);
        }
    }

    [Fact]
    public async Task ExternalProgramsRootRejectsAnAdditionalResourceJunctionWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Original"),
            [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("original"))],
            UpdatedAtUtc);
        var outsideDirectory = Path.Combine(scope.ApplicationRootPath, "outside-programs-root");
        Directory.CreateDirectory(outsideDirectory);
        var sentinelPath = Path.Combine(outsideDirectory, "sentinel.txt");
        await File.WriteAllTextAsync(sentinelPath, "must-survive");
        var junctionPath = Path.Combine(
            scope.ApplicationRootPath,
            "external-programs",
            "program.untrusted");
        await CreateJunctionAsync(junctionPath, outsideDirectory);

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await repository.ListAsync(scope));
            Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("must-survive", await File.ReadAllTextAsync(sentinelPath));
        }
        finally
        {
            Directory.Delete(junctionPath);
        }
    }

    [Fact]
    public async Task LockedStagingRemnantFailsClosedWithoutContaminatingExistingResource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var original = await repository.ImportDirectoryAsync(
            scope,
            ExecutableRequest("Original"),
            [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("original"))],
            UpdatedAtUtc);
        var stagingDirectory = Path.Combine(
            scope.ApplicationRootPath,
            "external-programs",
            $".{original.ResourceId}.staging");
        Directory.CreateDirectory(Path.Combine(stagingDirectory, "files"));
        var lockedPath = Path.Combine(stagingDirectory, "files", "stale.dll");
        await using (var locked = new FileStream(
                         lockedPath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            await locked.WriteAsync(Encoding.UTF8.GetBytes("stale"));
            await Assert.ThrowsAsync<IOException>(async () =>
                await repository.ImportDirectoryAsync(
                    scope,
                    ExecutableRequest("Replacement"),
                    [Upload("files/vendor-helper.exe", Encoding.UTF8.GetBytes("replacement"))],
                    UpdatedAtUtc.AddMinutes(1)));
        }

        var reopened = await repository.GetAsync(scope, original.ResourceId);
        Assert.NotNull(reopened);
        Assert.Equal(original.ContentSha256, reopened.ContentSha256);
        Assert.Equal("original", await File.ReadAllTextAsync(ResourceFilePath(
            scope,
            "files/vendor-helper.exe")));
        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("files/../escape.exe")]
    [InlineData("files\\escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("files/CON.txt")]
    [InlineData("files/trailing-dot./helper.exe")]
    [InlineData("files/invalid?.exe")]
    [InlineData("files/e\u0301/helper.exe")]
    public async Task SaveRejectsNonCanonicalUploadPath(string path)
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var bytes = Encoding.UTF8.GetBytes("payload");

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await repository.ImportDirectoryAsync(
                scope,
                ExecutableRequest("Invalid path"),
                [Upload(path, bytes)],
                UpdatedAtUtc));
    }

    [Theory]
    [InlineData("files/vendor-helper.cmd")]
    [InlineData("files/vendor-helper.ps1")]
    [InlineData("files/vendor-helper.py")]
    [InlineData("files/vendor-helper.dll")]
    public void DefinitionRejectsEntryPointThatCannotBeLaunchedByWindowsProcessHost(string entryPoint)
    {
        var request = ExecutableRequest("Unsupported entry point") with { EntryPoint = entryPoint };

        Assert.Throws<ArgumentException>(() => ExternalProgramResourceValidator.ValidateDefinition(request));
    }

    [Fact]
    public async Task DeleteCommitsByDirectoryRenameAndRemovesResource()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        var bytes = Encoding.UTF8.GetBytes("delete-me");
        await repository.ImportDirectoryAsync(
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
    public void FrozenInventoryPolicyAcceptsExactBoundaryAndRejectsCountSizeAndCumulativeOverflow()
    {
        Assert.Equal(
            ExternalProgramResourceContract.MaximumFrozenTotalBytes,
            ExternalProgramResourceContract.AccumulateFrozenFileBytes(
                ExternalProgramResourceContract.MaximumFrozenFileCount - 1,
                ExternalProgramResourceContract.MaximumFrozenTotalBytes
                    - ExternalProgramResourceContract.MaximumFrozenFileBytes,
                ExternalProgramResourceContract.MaximumFrozenFileBytes));
        Assert.Throws<InvalidDataException>(() =>
            ExternalProgramResourceContract.AccumulateFrozenFileBytes(
                ExternalProgramResourceContract.MaximumFrozenFileCount,
                0,
                0));
        Assert.Throws<InvalidDataException>(() =>
            ExternalProgramResourceContract.AccumulateFrozenFileBytes(
                0,
                0,
                ExternalProgramResourceContract.MaximumFrozenFileBytes + 1));
        Assert.Throws<InvalidDataException>(() =>
            ExternalProgramResourceContract.AccumulateFrozenFileBytes(
                4,
                ExternalProgramResourceContract.MaximumFrozenTotalBytes,
                1));
    }

    [Fact]
    public async Task ReadRejectsOversizedCraftedFileBeforeHashingPayload()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        await repository.SaveDefinitionAsync(scope, ProviderRequest("Crafted"), UpdatedAtUtc);
        var filePath = ResourceFilePath(scope, "files/oversized.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(ExternalProgramResourceContract.MaximumFrozenFileBytes + 1);
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetAsync(scope, "program.vendor-helper"));

        Assert.Contains("inventory exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadStopsAtFirstFileBeyondCraftedInventoryCountLimit()
    {
        var repository = new FileSystemExternalProgramResourceRepository();
        var scope = CreateScope();
        await repository.SaveDefinitionAsync(scope, ProviderRequest("Crafted"), UpdatedAtUtc);
        var filesRoot = Path.GetDirectoryName(ResourceFilePath(scope, "files/placeholder.bin"))!;
        Directory.CreateDirectory(filesRoot);
        for (var index = 0; index <= ExternalProgramResourceContract.MaximumFrozenFileCount; index++)
        {
            await File.WriteAllBytesAsync(
                Path.Combine(filesRoot, $"file-{index:D3}.bin"),
                []);
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.GetAsync(scope, "program.vendor-helper"));

        Assert.Contains("inventory exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void DefinitionAllowsCanonicalProductionInputSource()
    {
        var request = ExecutableRequest("Production input") with
        {
            InputMappings =
            [
                .. ExecutableRequest("Production input").InputMappings,
                new ExternalProgramInputMapping("$production.inspection.limit", "limit")
            ]
        };

        ExternalProgramResourceValidator.ValidateDefinition(request);
    }

    [Theory]
    [InlineData("$production.")]
    [InlineData("$production. input")]
    [InlineData("$production.input ")]
    [InlineData("$Production.input")]
    public void DefinitionRejectsMalformedProductionInputSource(string source)
    {
        var request = ExecutableRequest("Invalid Production input") with
        {
            InputMappings =
            [
                .. ExecutableRequest("Invalid Production input").InputMappings,
                new ExternalProgramInputMapping(source, "limit")
            ]
        };

        Assert.Throws<ArgumentException>(() =>
            ExternalProgramResourceValidator.ValidateDefinition(request));
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

    private static SaveExternalProgramResourceRequest ProviderRequest(string displayName) =>
        ExecutableRequest(displayName) with
        {
            LaunchKind = ExternalProgramLaunchKind.Provider,
            EntryPoint = null,
            ProviderKind = "ProcessCommandProvider",
            ProviderKey = "provider.vendor-helper"
        };

    private static ExternalProgramFileUpload Upload(string path, byte[] bytes) => new(
        path,
        new MemoryStream(bytes, writable: false),
        bytes.Length,
        Sha256(bytes));

    private static async Task CreateJunctionAsync(string junctionPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
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
                junctionPath,
                targetPath
            }
        }) ?? throw new InvalidOperationException("Could not start mklink for the repository security test.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create test junction: {await process.StandardError.ReadToEndAsync()}");
        }
    }

    private static string ResourceFilePath(ProjectApplicationWorkspaceScope scope, string relativePath) =>
        Path.Combine(
            scope.ApplicationRootPath,
            "external-programs",
            "program.vendor-helper",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

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
