using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.ContentProtection.Tests;

public sealed class ImmutableContentProtectorTests
{
    private const string TestStationServiceSid =
        "S-1-5-80-123-456-789-1011-1213";

    [Fact]
    public void CleanupDiscoveryRejectsUppercaseTransactionIdentifier()
    {
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-cleanup-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        var contentSha256 = new string('a', 64);
        var nonCanonical = Path.Combine(
            cacheRoot,
            $".{contentSha256}.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.committing");
        Directory.CreateDirectory(nonCanonical);
        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                ImmutableContentCacheCleanupDiscovery.DiscoverPackageContentHashes(cacheRoot));
            Assert.Contains("not a canonical transaction directory", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public void ServiceSidIsDerivedFromTheCanonicalWindowsServiceName()
    {
        Assert.Equal(
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "TrustedInstaller"));
        Assert.Equal(
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "TrustedInstaller"),
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                "trustedinstaller"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" OpenLineOpsAgent")]
    [InlineData("OpenLineOpsAgent ")]
    [InlineData("OpenLineOps/Agent")]
    [InlineData("OpenLineOps\\Agent")]
    [InlineData("OpenLineOps\nAgent")]
    [InlineData("OpenLineOps Agent")]
    [InlineData("OpenLineOps:Agent")]
    [InlineData("OpenLineOpsÅgent")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ServiceSidDerivationRejectsNoncanonicalServiceNames(string serviceName)
    {
        Assert.Throws<InvalidDataException>(() =>
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(serviceName));
    }

    [Fact]
    public void StationServiceIdentityRequiresTheCompleteRestrictedServiceTokenContract()
    {
        WindowsStationServiceIdentityReader.Validate(new WindowsStationServiceIdentity(
            WindowsStationServiceIdentityReader.LocalServiceSid,
            TestStationServiceSid,
            ServiceLogonSidEnabled: true,
            TokenHasRestrictions: true,
            ServiceSidEnabled: true,
            ServiceSidOwnerEligible: true,
            ServiceSidRestricted: true));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsTokenHasRestrictionsUsesItsNativeBooleanWidth()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);

        _ = WindowsStationServiceIdentityReader.ReadTokenHasRestrictions(
            identity.AccessToken);
    }

    [Theory]
    [InlineData("S-1-5-18", true, true, true, true, true)]
    [InlineData("S-1-5-19", false, true, true, true, true)]
    [InlineData("S-1-5-19", true, false, true, true, true)]
    [InlineData("S-1-5-19", true, true, false, true, true)]
    [InlineData("S-1-5-19", true, true, true, false, true)]
    [InlineData("S-1-5-19", true, true, true, true, false)]
    public void StationServiceIdentityRejectsAnyMissingTokenFact(
        string hostAccountSid,
        bool serviceLogonSidEnabled,
        bool tokenHasRestrictions,
        bool serviceSidEnabled,
        bool serviceSidOwnerEligible,
        bool serviceSidRestricted)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsStationServiceIdentityReader.Validate(new WindowsStationServiceIdentity(
                hostAccountSid,
                TestStationServiceSid,
                serviceLogonSidEnabled,
                tokenHasRestrictions,
                serviceSidEnabled,
                serviceSidOwnerEligible,
                serviceSidRestricted)));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsPolicyRequiresExactStationServiceSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var capabilitySid = WindowsAppContainerIdentity.EnsureCapabilitySid(
                WindowsAppContainerIdentity.ExternalProgramContentCapabilityName);
            var protector = new ImmutableContentProtector();

            var missingStationService = Assert.Throws<InvalidDataException>(() =>
                protector.ProtectCacheBoundary(
                    cacheRoot,
                    new ImmutableContentProtectionPolicy(capabilitySid)));
            Assert.Contains(
                "StationServiceSid is required",
                missingStationService.Message,
                StringComparison.Ordinal);

            var nonServiceIdentity = Assert.Throws<InvalidDataException>(() =>
                protector.ProtectCacheBoundary(
                    cacheRoot,
                    new ImmutableContentProtectionPolicy(capabilitySid, capabilitySid)));
            Assert.Contains("canonical Windows service SID", nonServiceIdentity.Message, StringComparison.Ordinal);

            var accountIdentity = Assert.Throws<InvalidDataException>(() =>
                protector.ProtectCacheBoundary(
                    cacheRoot,
                    new ImmutableContentProtectionPolicy(
                        capabilitySid,
                        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Value)));
            Assert.Contains(
                "canonical Windows service SID",
                accountIdentity.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cacheRoot);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CacheBoundaryReadOnlyVerificationDoesNotRequireStationServiceToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        Assert.NotEqual(
            WindowsStationServiceIdentityReader.LocalServiceSid,
            identity.User?.Value);
        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-cache-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            var policy = new ImmutableContentProtectionPolicy(
                WindowsAppContainerIdentity.EnsureCapabilitySid(
                    WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
                TestStationServiceSid);

            var exception = Assert.Throws<InvalidDataException>(() =>
                new ImmutableContentProtector().VerifyCacheBoundary(cacheRoot, policy));
            Assert.Contains("cache namespace", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cacheRoot);
        }
    }

    [Fact]
    public void ServiceSidDerivationAcceptsTheEntireCanonicalAsciiBoundary()
    {
        var serviceName = new string('A', 76) + "._-9";
        Assert.Equal(80, serviceName.Length);
        _ = WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(serviceName);
    }

    [Fact]
    public async Task VerificationRejectsDependencyTamperingAndExtraContent()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.DependencyPath, "tampered", Encoding.UTF8);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Protector.VerifyInventoryAsync(
                    fixture.ContentDirectory,
                    fixture.Inventory));

            await File.WriteAllBytesAsync(fixture.DependencyPath, fixture.DependencyBytes);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.ContentDirectory, "unexpected.txt"),
                "unexpected");
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Protector.VerifyInventoryAsync(
                    fixture.ContentDirectory,
                    fixture.Inventory));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Fact]
    public void StagingCleanupReturnsOnlyForATrulyAbsentCanonicalEntry()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"openlineops-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(
            cacheRoot,
            $".{new string('a', 64)}.{Guid.NewGuid():N}.installing");
        try
        {
            ImmutableContentProtector.DeleteUnprotectedStagingDirectory(cacheRoot, staging);
            Assert.Empty(Directory.EnumerateFileSystemEntries(cacheRoot));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [Fact]
    public void StagingCleanupRejectsAFileWithoutMutatingIt()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"openlineops-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(
            cacheRoot,
            $".{new string('b', 64)}.{Guid.NewGuid():N}.committing");
        File.WriteAllText(staging, "preserve");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                ImmutableContentProtector.DeleteUnprotectedStagingDirectory(
                    cacheRoot,
                    staging));
            Assert.Equal("preserve", File.ReadAllText(staging));
        }
        finally
        {
            File.Delete(staging);
            Directory.Delete(cacheRoot);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsStagingCleanupDeletesNestedReadOnlyTreeThroughStableHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-staging-handles-{Guid.NewGuid():N}");
        var staging = Path.Combine(
            cacheRoot,
            $".{new string('c', 64)}.{Guid.NewGuid():N}.installing");
        var nested = Path.Combine(staging, "one", "two");
        var payload = Path.Combine(nested, "payload.bin");
        Directory.CreateDirectory(nested);
        File.WriteAllText(payload, "delete-through-handle");
        File.SetAttributes(payload, File.GetAttributes(payload) | FileAttributes.ReadOnly);
        try
        {
            ImmutableContentProtector.DeleteUnprotectedStagingDirectory(cacheRoot, staging);

            Assert.False(Directory.Exists(staging));
            Assert.Empty(Directory.EnumerateFileSystemEntries(cacheRoot));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
            {
                BestEffortDeleteTestTree(cacheRoot);
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsStagingCleanupBlocksAConcurrentJunctionSwapAfterOpeningTheDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-staging-race-{Guid.NewGuid():N}");
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-staging-outside-{Guid.NewGuid():N}");
        var stagingLeaf = $".{new string('d', 64)}.{Guid.NewGuid():N}.committing";
        var staging = Path.Combine(cacheRoot, stagingLeaf);
        var mutable = Path.Combine(staging, "mutable");
        var detached = Path.Combine(staging, "detached");
        var outsideSentinel = Path.Combine(outsideRoot, "must-survive.txt");
        Directory.CreateDirectory(mutable);
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(mutable, "owned.txt"), "owned");
        File.WriteAllText(outsideSentinel, "outside");
        using var entryOpened = new ManualResetEventSlim(initialState: false);
        using var attackCompleted = new ManualResetEventSlim(initialState: false);
        var swapped = false;
        Exception? attackerFailure = null;
        Task attacker = Task.Run(() =>
        {
            try
            {
                if (!entryOpened.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException(
                        "Handle-bound deletion did not reach the junction-swap checkpoint.");
                }

                Directory.Move(mutable, detached);
                CreateDirectoryJunction(mutable, outsideRoot);
                swapped = true;
            }
            catch (Exception exception)
            {
                attackerFailure = exception;
            }
            finally
            {
                attackCompleted.Set();
            }
        });
        try
        {
            WindowsHandleBoundTreeDeletion.DeleteDirectChildTree(
                cacheRoot,
                stagingLeaf,
                maximumEntryCount: 100,
                maximumBytes: 1024 * 1024,
                afterEntryOpened: relativePath =>
                {
                    if (entryOpened.IsSet
                        || !string.Equals(
                            relativePath,
                            stagingLeaf + "/mutable",
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    entryOpened.Set();
                    if (!attackCompleted.Wait(TimeSpan.FromSeconds(10)))
                    {
                        throw new TimeoutException(
                            "Concurrent junction-swap attacker did not finish.");
                    }
                });

            Assert.False(swapped);
            Assert.IsType<IOException>(attackerFailure);
            Assert.False(Directory.Exists(staging));
            Assert.Equal("outside", File.ReadAllText(outsideSentinel));
        }
        finally
        {
            entryOpened.Set();
            await attacker;
            if (Directory.Exists(mutable)
                && (File.GetAttributes(mutable) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(mutable, recursive: false);
            }
            if (Directory.Exists(cacheRoot))
            {
                BestEffortDeleteTestTree(cacheRoot);
            }
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsStagingCleanupRejectsAFileHardLinkedOutsideTheCache()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cacheRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-staging-hardlink-{Guid.NewGuid():N}");
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"openlineops-staging-hardlink-outside-{Guid.NewGuid():N}");
        var staging = Path.Combine(
            cacheRoot,
            $".{new string('e', 64)}.{Guid.NewGuid():N}.installing");
        var outsideSentinel = Path.Combine(outsideRoot, "must-survive.bin");
        var linkedPayload = Path.Combine(staging, "linked.bin");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(outsideSentinel, "outside-hardlink");
        if (!CreateHardLink(linkedPayload, outsideSentinel, IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not create the staging cleanup hard-link test alias.");
        }

        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                ImmutableContentProtector.DeleteUnprotectedStagingDirectory(
                    cacheRoot,
                    staging));

            Assert.Contains("exactly one hard link", error.Message, StringComparison.Ordinal);
            Assert.Equal("outside-hardlink", File.ReadAllText(outsideSentinel));
            Assert.True(File.Exists(linkedPayload));
        }
        finally
        {
            if (File.Exists(linkedPayload))
            {
                File.Delete(linkedPayload);
            }
            if (Directory.Exists(cacheRoot))
            {
                Directory.Delete(cacheRoot, recursive: true);
            }
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CacheTransactionLockCancelsAQueuedAcquisitionWithoutLeakingOwnership()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"openlineops-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cacheRoot);
        try
        {
            using var firstLock = new ImmutableContentCacheTransactionLock(
                cacheRoot,
                TestStationServiceSid);
            using var secondLock = new ImmutableContentCacheTransactionLock(
                cacheRoot,
                TestStationServiceSid);
            await using ImmutableContentCacheTransactionLease firstLease =
                await firstLock.AcquireAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await secondLock.AcquireAsync(cancellation.Token));

            await firstLease.DisposeAsync();
            await using ImmutableContentCacheTransactionLease recoveredLease =
                await secondLock.AcquireAsync();
        }
        finally
        {
            Directory.Delete(cacheRoot);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task CacheTransactionLockRecoversAfterOwningProcessIsKilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cacheRoot = Path.Combine(Path.GetTempPath(), $"openlineops-lock-{Guid.NewGuid():N}");
        var readyPath = Path.Combine(Path.GetTempPath(), $"openlineops-lock-ready-{Guid.NewGuid():N}");
        Process? process = null;
        Directory.CreateDirectory(cacheRoot);
        try
        {
            using var transactionLock = new ImmutableContentCacheTransactionLock(
                cacheRoot,
                TestStationServiceSid);
            var identity = ImmutableContentProtector.GetStableDirectoryIdentity(cacheRoot);
            var name = "Global\\OpenLineOps.PackageCache."
                       + Convert.ToHexStringLower(SHA256.HashData(
                           Encoding.UTF8.GetBytes(identity)));
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "WindowsPowerShell",
                string.Concat("v", 1, ".0"),
                "powershell.exe");
            process = Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment =
                {
                    ["OPENLINEOPS_MUTEX_NAME"] = name,
                    ["OPENLINEOPS_MUTEX_READY"] = readyPath
                },
                ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$m=[Threading.Mutex]::OpenExisting($env:OPENLINEOPS_MUTEX_NAME);"
                    + "$null=$m.WaitOne();"
                    + "[IO.File]::WriteAllText($env:OPENLINEOPS_MUTEX_READY,'ready');"
                    + "[Threading.Thread]::Sleep([Threading.Timeout]::Infinite)"
                }
            }) ?? throw new InvalidOperationException(
                "Could not start the abandoned cache-mutex proof process.");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(readyPath) && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Cache-mutex proof process exited early with {process.ExitCode}.");
                }

                await Task.Delay(25);
            }

            Assert.True(File.Exists(readyPath));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();

            await using ImmutableContentCacheTransactionLease recovered =
                await transactionLock.AcquireAsync();
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }

                process.Dispose();
            }

            File.Delete(readyPath);
            Directory.Delete(cacheRoot);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task InventoryVerificationRejectsAnExternalHardLinkAlias()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = await CreateFixtureAsync();
        var alias = Path.Combine(Path.GetTempPath(), $"openlineops-hardlink-{Guid.NewGuid():N}.bin");
        try
        {
            if (!CreateHardLink(alias, fixture.DependencyPath, IntPtr.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Could not create the immutable-content hard-link test alias.");
            }
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await fixture.Protector.VerifyInventoryAsync(
                    fixture.ContentDirectory,
                    fixture.Inventory));
        }
        finally
        {
            File.Delete(alias);
            fixture.Delete();
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task InventoryVerificationRejectsAlternateDataStreams()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"openlineops-long-stream-{Guid.NewGuid():N}");
        var relativePath = string.Join(
            '/',
            Enumerable.Range(0, 7).Select(index => $"segment-{index}-{new string('x', 32)}"))
                           + "/payload.bin";
        var payload = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);
        byte[] bytes = Encoding.UTF8.GetBytes("long-path-stream");
        await File.WriteAllBytesAsync(payload, bytes);
        var inventory = new[]
        {
            new ImmutableContentFile(
                relativePath,
                bytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(bytes)))
        };
        try
        {
            Assert.True(payload.Length > 260);
            var protector = new ImmutableContentProtector();
            await protector.VerifyInventoryAsync(root, inventory);
            await File.WriteAllTextAsync(payload + ":unexpected", "stream");
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await protector.VerifyInventoryAsync(root, inventory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ContentFixture> CreateFixtureAsync()
    {
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"openlineops-cache-{Guid.NewGuid():N}");
        var contentDirectory = Path.Combine(cacheRoot, new string('a', 64));
        var dependencyPath = Path.Combine(contentDirectory, "runtime", "dependency.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dependencyPath)!);
        var dependencyBytes = Encoding.UTF8.GetBytes("immutable dependency");
        await File.WriteAllBytesAsync(dependencyPath, dependencyBytes);
        var inventory = new[]
        {
            new ImmutableContentFile(
                "runtime/dependency.bin",
                dependencyBytes.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(dependencyBytes)))
        };
        return new ContentFixture(
            cacheRoot,
            contentDirectory,
            dependencyPath,
            dependencyBytes,
            inventory,
            new ImmutableContentProtectionPolicy("inventory-reader"),
            new ImmutableContentProtector());
    }

    private static void BestEffortDeleteTestTree(string root)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         root,
                         "*",
                         SearchOption.AllDirectories))
            {
                File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            }

            File.SetAttributes(root, File.GetAttributes(root) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            // Restricted-token SCM tests deliberately leave sealed content for the elevated harness.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
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
        }) ?? throw new InvalidOperationException(
            "Could not start mklink for the handle-bound deletion security test.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create the handle-bound deletion test junction: "
                + process.StandardError.ReadToEnd());
        }
    }

    private sealed record ContentFixture(
        string CacheRoot,
        string ContentDirectory,
        string DependencyPath,
        byte[] DependencyBytes,
        IReadOnlyCollection<ImmutableContentFile> Inventory,
        ImmutableContentProtectionPolicy Policy,
        ImmutableContentProtector Protector)
    {
        public void Delete()
        {
            if (Directory.Exists(ContentDirectory))
            {
                BestEffortDeleteTestTree(ContentDirectory);
            }

            if (Directory.Exists(CacheRoot))
            {
                Directory.Delete(CacheRoot);
            }
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
