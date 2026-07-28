using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OpenLineOps.ContentProtection;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceTokenTestRelayContractTests
{
    private static readonly JsonSerializerOptions RelayJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    [Fact]
    public void RelayBundleIsOneNativeAotExecutableWithoutDirectUser32Import()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var relayExecutable = RequiredRelayExecutable();
        var relayRoot = Path.GetDirectoryName(relayExecutable)
                        ?? throw new InvalidDataException(
                            "The staged Test Relay has no bundle root.");
        Assert.Equal(
            [Path.GetFileName(relayExecutable)],
            Directory.EnumerateFiles(relayRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(relayRoot, path))
                .Order(StringComparer.Ordinal)
                .ToArray());

        using (var stream = File.OpenRead(relayExecutable))
        using (var pe = new PEReader(stream))
        {
            Assert.False(pe.HasMetadata);
            Assert.Null(pe.PEHeaders.CorHeader);
            Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        }

        var importedModules = ReadPeImportedModules(relayExecutable);
        Assert.DoesNotContain(
            "USER32.dll",
            importedModules,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "coreclr.dll",
            importedModules,
            StringComparer.OrdinalIgnoreCase);

        var imageBytes = File.ReadAllBytes(relayExecutable);
        AssertImageContainsText(imageBytes, "user32.dll");
        AssertImageContainsText(imageBytes, "GetProcessWindowStation");
        AssertImageContainsText(imageBytes, "GetThreadDesktop");
        AssertImageContainsText(imageBytes, "GetUserObjectInformationW");
    }

    [Fact]
    public void RelayBundleCopyIsFrozenAndRejectsChangedOrAddedFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(source, "nested")).FullName;
        File.WriteAllText(Path.Combine(source, "relay.exe"), "relay");
        File.WriteAllText(Path.Combine(nested, "runtime.dll"), "runtime");
        var destination = Path.Combine(root.Path, "destination");

        var inventory = WindowsServiceTokenTestBridge.CopyRelayBundle(
            source,
            destination);
        WindowsServiceTokenTestBridge.VerifyRelayBundle(destination, inventory);

        File.AppendAllText(Path.Combine(destination, "relay.exe"), "-changed");
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.VerifyRelayBundle(
                destination,
                inventory));

        Directory.Delete(destination, recursive: true);
        inventory = WindowsServiceTokenTestBridge.CopyRelayBundle(
            source,
            destination);
        File.WriteAllText(Path.Combine(destination, "unexpected.dll"), "unexpected");
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.VerifyRelayBundle(
                destination,
                inventory));
    }

    [Fact]
    public void RelayBundleCopyRejectsAnEmptyBundle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var source = Directory.CreateDirectory(Path.Combine(root.Path, "source")).FullName;
        var destination = Path.Combine(root.Path, "destination");

        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.CopyRelayBundle(
                source,
                destination));
    }

    [Fact]
    public void RelayTreeOwnerCanonicalizationRestoresEveryEntry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var child = Directory.CreateDirectory(Path.Combine(root.Path, "child"));
        var file = Path.Combine(child.FullName, "payload.bin");
        File.WriteAllText(file, "payload");

        WindowsServiceTokenTestBridge.CanonicalizeRelayTreeOwner(root.Path);

        var expected = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(expected);
        Assert.Equal(expected, ReadOwner(new DirectoryInfo(root.Path)));
        Assert.Equal(expected, ReadOwner(child));
        Assert.Equal(expected, ReadOwner(new FileInfo(file)));
    }

    [Fact]
    public void ExactProcessHandleRejectsPidAndCreationTimeDrift()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var pid = checked((uint)process.Id);
        var createdAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;

        WindowsServiceTokenTestBridge.ValidateExactProcessHandle(
            process.SafeHandle,
            pid,
            createdAtUtcTicks,
            "contract process");
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.ValidateExactProcessHandle(
                process.SafeHandle,
                pid == uint.MaxValue ? pid - 1 : pid + 1,
                createdAtUtcTicks,
                "contract process"));
        Assert.Throws<InvalidDataException>(
            () => WindowsServiceTokenTestBridge.ValidateExactProcessHandle(
                process.SafeHandle,
                pid,
                createdAtUtcTicks + 1,
                "contract process"));
    }

    [Fact]
    public void CompareObjectHandlesLoadsFromDocumentedKernelBaseDll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = Process.GetCurrentProcess();

        Assert.True(
            WindowsServiceTokenTestBridge.CompareObjectHandles(
                process.SafeHandle,
                process.SafeHandle));
    }

    [Fact]
    public void PipeClientRightsContainOnlyProtocolAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            System.IO.Pipes.PipeAccessRights.ReadWrite
            | System.IO.Pipes.PipeAccessRights.Synchronize,
            WindowsServiceTokenTestBridge.AuthenticatedPipeClientRights);
        Assert.False(
            WindowsServiceTokenTestBridge.AuthenticatedPipeClientRights.HasFlag(
                System.IO.Pipes.PipeAccessRights.ChangePermissions));
        Assert.False(
            WindowsServiceTokenTestBridge.AuthenticatedPipeClientRights.HasFlag(
                System.IO.Pipes.PipeAccessRights.TakeOwnership));
    }

    [Fact]
    public void RelayExecutableRejectsEveryInvocationExceptOneCanonicalRequest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var relayExecutable = RequiredRelayExecutable();
        Assert.Equal(64, RunRelay(relayExecutable, []));
        Assert.Equal(64, RunRelay(relayExecutable, ["--unknown", relayExecutable]));
        Assert.Equal(64, RunRelay(relayExecutable, ["--request"]));
        Assert.Equal(
            64,
            RunRelay(
                relayExecutable,
                ["--request", relayExecutable, "unexpected"]));
    }

    [Fact]
    public void RelayExecutableRejectsUnknownAndMissingRequestProperties()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var relayExecutable = RequiredRelayExecutable();
        var missingPath = Path.Combine(root.Path, "missing.json");
        File.WriteAllText(missingPath, "{}");
        Assert.Equal(70, RunRelay(relayExecutable, ["--request", missingPath]));

        var unknownPath = Path.Combine(root.Path, "unknown.json");
        File.WriteAllText(
            unknownPath,
            """
            {
              "nonce": "0000000000000000000000000000000000000000000000000000000000000000",
              "sourceProcessId": 1,
              "sourceProcessCreatedAtUtcTicks": 621355968000000000,
              "sourceExecutablePath": "C:\\Windows\\System32\\notepad.exe",
              "sourceExecutableSha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "expectedSourceServiceSid": "S-1-5-80-1-2-3-4-5",
              "relayBundleRoot": "C:\\Windows",
              "relayExecutablePath": "C:\\Windows\\OpenLineOps.WindowsServiceToken.TestRelay.exe",
              "relayExecutableSha256": "0000000000000000000000000000000000000000000000000000000000000000",
              "controlPipeName": "openlineops-source-token-relay-contract",
              "unknown": true
            }
            """);
        Assert.Equal(70, RunRelay(relayExecutable, ["--request", unknownPath]));
    }

    [Fact]
    public void CanonicalRelayRequestReachesTokenSelfAttestation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        using var source = Process.GetCurrentProcess();
        var relayExecutable = RequiredRelayExecutable();
        var relayBundleRoot = Path.GetDirectoryName(relayExecutable)
                              ?? throw new InvalidDataException(
                                  "The staged Test Relay has no bundle root.");
        var requestPath = Path.Combine(root.Path, "request.json");
        var request = CreateControllerRequest(
            requestPath,
            relayBundleRoot,
            relayExecutable,
            source);
        WriteRelayRequest(requestPath, request);

        var outcome = RunRelayWithOutput(
            relayExecutable,
            ["--request", requestPath]);

        Assert.Equal(70, outcome.ExitCode);
        Assert.Contains(
            "exact primary, unlinked, restricted LocalService identity",
            outcome.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "canonical relay pipe name",
            outcome.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuspendedRelayIsBoundToItsImageAndKilledByJobDisposal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var root = new TemporaryDirectory();
        var relayExecutable = RequiredRelayExecutable();
        var relayBundleRoot = Path.GetDirectoryName(relayExecutable)
                              ?? throw new InvalidDataException(
                                  "The staged Test Relay has no bundle root.");
        var requestPath = Path.Combine(root.Path, "request.json");
        File.WriteAllText(requestPath, "{}");
        using var source = Process.GetCurrentProcess();
        var request = CreateControllerRequest(
            requestPath,
            relayBundleRoot,
            relayExecutable,
            source);
        var runnerSid = WindowsIdentity.GetCurrent().User
                        ?? throw new InvalidOperationException(
                            "The relay contract runner has no SID.");
        uint relayProcessId;
        using (var relay = WindowsSourceTokenRelayProcess.CreateSuspended(
                   request,
                   source.SafeHandle,
                   runnerSid))
        {
            relayProcessId = relay.ProcessId;
            relay.ValidateCreated(request);
            relay.ValidateRunning(request);
        }

        AssertEventuallyProcessMissing(relayProcessId);
    }

    private static WindowsSourceTokenRelayRequest CreateControllerRequest(
        string requestPath,
        string relayBundleRoot,
        string relayExecutable,
        Process source)
    {
        var serviceName = "OpenLineOpsRelayContract";
        return new WindowsSourceTokenRelayRequest(
            requestPath,
            new string('0', 64),
            checked((uint)source.Id),
            source.StartTime.ToUniversalTime().Ticks,
            source.MainModule?.FileName
            ?? throw new InvalidDataException("The contract process image is unavailable."),
            Sha256File(
                source.MainModule?.FileName
                ?? throw new InvalidDataException(
                    "The contract process image is unavailable.")),
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(serviceName),
            relayBundleRoot,
            relayExecutable,
            Sha256File(relayExecutable),
            "openlineops-source-token-relay-contract");
    }

    private static List<string> ReadPeImportedModules(string executablePath)
    {
        using var stream = File.OpenRead(executablePath);
        using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
        var peHeader = pe.PEHeaders.PEHeader
                       ?? throw new InvalidDataException(
                           "The staged Test Relay has no PE header.");
        var directory = peHeader.ImportTableDirectory;
        if (directory.RelativeVirtualAddress == 0 || directory.Size < 20)
        {
            throw new InvalidDataException(
                "The staged Test Relay has no valid PE import directory.");
        }

        var descriptorOffset = RvaToFileOffset(
            pe.PEHeaders,
            directory.RelativeVirtualAddress);
        var descriptorCount = directory.Size / 20;
        var modules = new List<string>(descriptorCount);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        for (var index = 0; index < descriptorCount; index++)
        {
            stream.Position = checked(descriptorOffset + index * 20L);
            var originalFirstThunk = reader.ReadUInt32();
            var timestamp = reader.ReadUInt32();
            var forwarderChain = reader.ReadUInt32();
            var nameRva = reader.ReadUInt32();
            var firstThunk = reader.ReadUInt32();
            if (originalFirstThunk == 0
                && timestamp == 0
                && forwarderChain == 0
                && nameRva == 0
                && firstThunk == 0)
            {
                return modules;
            }

            if (nameRva == 0)
            {
                throw new InvalidDataException(
                    "The staged Test Relay contains a PE import descriptor without a module name.");
            }

            stream.Position = RvaToFileOffset(pe.PEHeaders, checked((int)nameRva));
            modules.Add(ReadNullTerminatedAscii(reader, 260));
        }

        throw new InvalidDataException(
            "The staged Test Relay PE import directory has no terminating descriptor.");
    }

    private static void AssertImageContainsText(
        byte[] imageBytes,
        string expectedText)
    {
        var asciiBytes = Encoding.ASCII.GetBytes(expectedText);
        var unicodeBytes = Encoding.Unicode.GetBytes(expectedText);
        Assert.True(
            imageBytes.AsSpan().IndexOf(asciiBytes) >= 0
            || imageBytes.AsSpan().IndexOf(unicodeBytes) >= 0,
            $"The staged Test Relay image is missing the marker '{expectedText}'.");
    }

    private static long RvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var sectionSize = Math.Max(section.VirtualSize, section.SizeOfRawData);
            var sectionEnd = checked(section.VirtualAddress + sectionSize);
            if (rva >= section.VirtualAddress && rva < sectionEnd)
            {
                return checked(
                    (long)section.PointerToRawData + rva - section.VirtualAddress);
            }
        }

        throw new InvalidDataException(
            $"The staged Test Relay PE RVA 0x{rva:x8} is outside every section.");
    }

    private static string ReadNullTerminatedAscii(
        BinaryReader reader,
        int maximumBytes)
    {
        var bytes = new List<byte>(maximumBytes);
        for (var index = 0; index < maximumBytes; index++)
        {
            var value = reader.ReadByte();
            if (value == 0)
            {
                if (bytes.Count == 0 || bytes.Any(character => character > 0x7f))
                {
                    throw new InvalidDataException(
                        "The staged Test Relay contains an invalid PE import module name.");
                }

                return Encoding.ASCII.GetString([.. bytes]);
            }

            bytes.Add(value);
        }

        throw new InvalidDataException(
            "The staged Test Relay PE import module name is not null-terminated.");
    }

    private static void WriteRelayRequest(
        string path,
        WindowsSourceTokenRelayRequest request)
    {
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    request.Nonce,
                    request.SourceProcessId,
                    request.SourceProcessCreatedAtUtcTicks,
                    request.SourceExecutablePath,
                    request.SourceExecutableSha256,
                    request.ExpectedSourceServiceSid,
                    request.RelayBundleRoot,
                    request.RelayExecutablePath,
                    request.RelayExecutableSha256,
                    request.ControlPipeName
                },
                RelayJsonOptions));
    }

    private static string RequiredRelayExecutable()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "windows-service-token-test-relay",
            "OpenLineOps.WindowsServiceToken.TestRelay.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                "The staged Windows service-token Test Relay executable is missing.",
                path);
    }

    private static int RunRelay(
        string relayExecutable,
        IReadOnlyList<string> arguments) =>
        RunRelayWithOutput(relayExecutable, arguments).ExitCode;

    private static RelayProcessOutcome RunRelayWithOutput(
        string relayExecutable,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = relayExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "The Test Relay did not start.");
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Test Relay invocation did not exit.");
        }
        return new RelayProcessOutcome(
            process.ExitCode,
            process.StandardError.ReadToEnd());
    }

    private sealed record RelayProcessOutcome(int ExitCode, string StandardError);

    private static SecurityIdentifier ReadOwner(FileSystemInfo entry)
    {
        FileSystemSecurity security = entry is DirectoryInfo directory
            ? FileSystemAclExtensions.GetAccessControl(
                directory,
                AccessControlSections.Owner)
            : FileSystemAclExtensions.GetAccessControl(
                (FileInfo)entry,
                AccessControlSections.Owner);
        return security.GetOwner(typeof(SecurityIdentifier))
               as SecurityIdentifier
               ?? throw new InvalidDataException(
                   $"The contract entry '{entry.FullName}' has no SID owner.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void AssertEventuallyProcessMissing(uint processId)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            Thread.Sleep(25);
        }
        throw new InvalidOperationException(
            $"Suspended Test Relay PID {processId} survived its kill-on-close job.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "openlineops-relay-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
