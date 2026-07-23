using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using OpenLineOps.ContentProtection;
using OpenLineOps.ProcessIsolation;

namespace OpenLineOps.Agent.Tests;

public sealed partial class StagedAgentRabbitMqProcessE2ETests
{
    private const int MaximumPackagedContentCacheCommandOutputCharacters =
        64 * 1024;
    private static readonly TimeSpan PackagedContentCacheCommandTimeout =
        TimeSpan.FromSeconds(30);

    [SupportedOSPlatform("windows")]
    private static bool ProvisionPackageCacheThroughPackagedAgent(
        string packagedAgentExecutablePath,
        string packageCacheRoot,
        string stationServiceName,
        string expectedStationServiceSid)
    {
        var context = RequirePackagedContentCacheCommandContext(
            packagedAgentExecutablePath,
            packageCacheRoot,
            stationServiceName,
            expectedStationServiceSid);
        var result = RunPackagedContentCacheCommand(
            context,
            "--provision-content-cache");
        RequireExactPackagedContentCacheCommandResult(
            result,
            expectedExitCode: 0,
            expectedStandardOutput:
                $"Provisioned Station content-cache namespace at '{context.PackageCacheRoot}'."
                + Environment.NewLine,
            expectedStandardError: string.Empty,
            "provision the Station content-cache namespace");

        var anchor = Directory.GetParent(context.PackageCacheRoot)?.FullName
                     ?? throw new InvalidDataException(
                         "The provisioned Station package cache has no dedicated namespace anchor.");
        if (!Directory.Exists(anchor)
            || !Directory.Exists(context.PackageCacheRoot))
        {
            throw new InvalidDataException(
                "The packaged Station Agent reported successful cache provisioning without creating the exact namespace.");
        }

        new ImmutableContentProtector().VerifyCacheBoundary(
            context.PackageCacheRoot,
            context.Policy);
        string[] anchorEntries = [.. Directory.EnumerateFileSystemEntries(anchor)];
        if (anchorEntries.Length != 1
            || !string.Equals(
                Path.GetFullPath(anchorEntries[0]),
                context.PackageCacheRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The packaged Station Agent did not provision an exact dedicated cache namespace.");
        }

        if (Directory.EnumerateFileSystemEntries(context.PackageCacheRoot).Any())
        {
            throw new InvalidDataException(
                "A newly provisioned Station package cache must be empty.");
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool VerifyPackagedProvisionRejectsRunningService(
        string packagedAgentExecutablePath,
        string packageCacheRoot,
        string stationServiceName,
        string expectedStationServiceSid)
    {
        var context = RequirePackagedContentCacheCommandContext(
            packagedAgentExecutablePath,
            packageCacheRoot,
            stationServiceName,
            expectedStationServiceSid);
        new ImmutableContentProtector().VerifyCacheBoundary(
            context.PackageCacheRoot,
            context.Policy);
        var cacheIdentityBeforeCommand =
            ImmutableContentProtector.GetStableDirectoryIdentity(
                context.PackageCacheRoot);
        if (Directory.EnumerateFileSystemEntries(context.PackageCacheRoot).Any())
        {
            throw new InvalidDataException(
                "The running-service administration rejection must be exercised before any package installation.");
        }

        var result = RunPackagedContentCacheCommand(
            context,
            "--provision-content-cache");
        RequireExactPackagedContentCacheCommandResult(
            result,
            expectedExitCode: 70,
            expectedStandardOutput: string.Empty,
            expectedStandardError:
                $"OpenLineOps Station Agent terminated: Station service '{context.StationServiceName}' must be fully stopped before immutable cache administration."
                + Environment.NewLine,
            "reject cache provisioning while the exact Station service is running");

        new ImmutableContentProtector().VerifyCacheBoundary(
            context.PackageCacheRoot,
            context.Policy);
        var cacheIdentityAfterCommand =
            ImmutableContentProtector.GetStableDirectoryIdentity(
                context.PackageCacheRoot);
        if (!string.Equals(
                cacheIdentityBeforeCommand,
                cacheIdentityAfterCommand,
                StringComparison.Ordinal)
            || Directory.EnumerateFileSystemEntries(
                context.PackageCacheRoot).Any())
        {
            throw new InvalidDataException(
                "The rejected running-service administration command changed the provisioned cache namespace.");
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static void RequireFormalContentCacheRemovalScenario(
        string packageCacheRoot,
        string committedContentSha256,
        string preSealRecoveryContentSha256)
    {
        var cacheRoot = RequireCanonicalPackageCacheRoot(packageCacheRoot);
        RequireCanonicalContentSha256(
            committedContentSha256,
            nameof(committedContentSha256));
        RequireCanonicalContentSha256(
            preSealRecoveryContentSha256,
            nameof(preSealRecoveryContentSha256));
        if (string.Equals(
                committedContentSha256,
                preSealRecoveryContentSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The committed package and pre-seal recovery package must use different content hashes.");
        }

        RequirePackageCacheEntryState(
            cacheRoot,
            committedContentSha256,
            contentExpected: true,
            markerExpected: true,
            "committed signed package");
        RequirePackageCacheEntryState(
            cacheRoot,
            preSealRecoveryContentSha256,
            contentExpected: true,
            markerExpected: false,
            "interrupted pre-seal recovery package");
    }

    [SupportedOSPlatform("windows")]
    private static bool RemovePackageInstallationsThroughPackagedAgent(
        string packagedAgentExecutablePath,
        string packageCacheRoot,
        string stationServiceName,
        string expectedStationServiceSid)
    {
        if (!Directory.Exists(packageCacheRoot))
        {
            return false;
        }

        var context = RequirePackagedContentCacheCommandContext(
            packagedAgentExecutablePath,
            packageCacheRoot,
            stationServiceName,
            expectedStationServiceSid);
        var protectedPackages = EnumerateProtectedPackageStates(
            context.PackageCacheRoot);
        foreach (var package in protectedPackages)
        {
            RequirePackageCacheEntryState(
                context.PackageCacheRoot,
                package.ContentSha256,
                package.ContentPresent,
                package.MarkerPresent,
                "pre-removal snapshot");
            var result = RunPackagedContentCacheCommand(
                context,
                "--remove-content-cache-package",
                package.ContentSha256);
            RequireExactPackagedContentCacheCommandResult(
                result,
                expectedExitCode: 0,
                expectedStandardOutput:
                    $"Removed protected Station package '{package.ContentSha256}' from the content cache."
                    + Environment.NewLine,
                expectedStandardError: string.Empty,
                $"remove protected Station package '{package.ContentSha256}'");
            RequirePackageCacheEntryState(
                context.PackageCacheRoot,
                package.ContentSha256,
                contentExpected: false,
                markerExpected: false,
                "packaged Agent removal result");
        }

        ProtectedPackageState[] remaining = EnumerateProtectedPackageStates(
            context.PackageCacheRoot);
        if (remaining.Length != 0)
        {
            throw new InvalidDataException(
                "The packaged Station Agent left protected content or commit-marker directories after paired removal: "
                + string.Join(", ", remaining.Select(static package => package.ContentSha256)));
        }

        new ImmutableContentProtector().VerifyCacheBoundary(
            context.PackageCacheRoot,
            context.Policy);

        return protectedPackages.Length != 0;
    }

    [SupportedOSPlatform("windows")]
    private static PackagedContentCacheCommandContext
        RequirePackagedContentCacheCommandContext(
            string packagedAgentExecutablePath,
            string packageCacheRoot,
            string stationServiceName,
            string expectedStationServiceSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The staged packaged Station Agent content-cache gate requires Windows.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(packagedAgentExecutablePath);
        if (!Path.IsPathFullyQualified(packagedAgentExecutablePath))
        {
            throw new InvalidDataException(
                "The staged Station Agent executable path must be fully qualified.");
        }

        var executablePath = Path.GetFullPath(packagedAgentExecutablePath);
        if (!string.Equals(
                packagedAgentExecutablePath,
                executablePath,
                StringComparison.Ordinal)
            || !string.Equals(
                Path.GetFileName(executablePath),
                "OpenLineOps.Agent.exe",
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executablePath))
        {
            throw new FileNotFoundException(
                "The staged content-cache gate requires the exact canonical packaged OpenLineOps.Agent.exe path.",
                executablePath);
        }

        var cacheRoot = RequireCanonicalPackageCacheRoot(packageCacheRoot);
        var serviceName =
            WindowsStationServiceIdentityReader.RequireCanonicalServiceName(
                stationServiceName,
                nameof(stationServiceName));
        var serviceSid =
            WindowsStationServiceIdentityReader.RequireCanonicalServiceSid(
                expectedStationServiceSid,
                nameof(expectedStationServiceSid));
        var derivedServiceSid =
            WindowsStationServiceIdentityReader.ServiceSidFromNameRequired(
                serviceName);
        if (!string.Equals(
                serviceSid,
                derivedServiceSid,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The staged Station Agent executable command does not target the exact installed service SID.");
        }

        return new PackagedContentCacheCommandContext(
            executablePath,
            cacheRoot,
            serviceName,
            new ImmutableContentProtectionPolicy(
                WindowsAppContainerIdentity.EnsureCapabilitySid(
                    WindowsAppContainerIdentity.ExternalProgramContentCapabilityName),
                serviceSid));
    }

    private static string RequireCanonicalPackageCacheRoot(string packageCacheRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageCacheRoot);
        if (!Path.IsPathFullyQualified(packageCacheRoot))
        {
            throw new InvalidDataException(
                "The staged Station package cache path must be fully qualified.");
        }

        var canonical = Path.GetFullPath(packageCacheRoot);
        if (!string.Equals(packageCacheRoot, canonical, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The staged Station package cache path must already be canonical.");
        }

        return canonical;
    }

    private static PackagedContentCacheCommandResult RunPackagedContentCacheCommand(
        PackagedContentCacheCommandContext context,
        params string[] commandArguments) =>
        RunPackagedContentCacheCommandAsync(context, commandArguments)
            .GetAwaiter()
            .GetResult();

    private static async Task<PackagedContentCacheCommandResult>
        RunPackagedContentCacheCommandAsync(
            PackagedContentCacheCommandContext context,
            string[] commandArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = context.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(context.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in commandArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add("--OpenLineOps:WindowsServiceName");
        startInfo.ArgumentList.Add(context.StationServiceName);
        startInfo.ArgumentList.Add(
            "--OpenLineOps:Agent:PackageCacheDirectory");
        startInfo.ArgumentList.Add(context.PackageCacheRoot);
        foreach (var name in startInfo.Environment.Keys.Where(name =>
                     name.StartsWith(
                         "OpenLineOps__Agent__",
                         StringComparison.OrdinalIgnoreCase)
                     || string.Equals(
                         name,
                         "OpenLineOps__WindowsServiceName",
                         StringComparison.OrdinalIgnoreCase)
                     || string.Equals(
                         name,
                         "DOTNET_ENVIRONMENT",
                         StringComparison.OrdinalIgnoreCase)
                     || string.Equals(
                         name,
                         "DOTNET_CONTENTROOT",
                         StringComparison.OrdinalIgnoreCase)
                     || string.Equals(
                         name,
                         "ASPNETCORE_ENVIRONMENT",
                         StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }
        startInfo.Environment["DOTNET_CONTENTROOT"] =
            startInfo.WorkingDirectory;

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                "The packaged Station Agent content-cache command could not be started.");
        process.StandardInput.Close();
        Task<string> standardOutput = ReadBoundedPackagedCommandOutputAsync(
            process.StandardOutput,
            "stdout");
        Task<string> standardError = ReadBoundedPackagedCommandOutputAsync(
            process.StandardError,
            "stderr");
        Task processExit = process.WaitForExitAsync();
        Task timeout = Task.Delay(PackagedContentCacheCommandTimeout);
        Task never = Task.Delay(Timeout.InfiniteTimeSpan);
        Task processExitMonitor = processExit;
        Task standardOutputMonitor = standardOutput;
        Task standardErrorMonitor = standardError;
        var processExited = false;
        var standardOutputCompleted = false;
        var standardErrorCompleted = false;
        while (!processExited
               || !standardOutputCompleted
               || !standardErrorCompleted)
        {
            Task completed = await Task.WhenAny(
                    processExitMonitor,
                    standardOutputMonitor,
                    standardErrorMonitor,
                    timeout)
                .ConfigureAwait(false);
            if (ReferenceEquals(completed, timeout))
            {
                await TerminatePackagedContentCacheCommandAsync(process)
                    .ConfigureAwait(false);
                await ObservePackagedContentCacheCommandTaskAsync(standardOutput)
                    .ConfigureAwait(false);
                await ObservePackagedContentCacheCommandTaskAsync(standardError)
                    .ConfigureAwait(false);
                throw new TimeoutException(
                    $"The packaged Station Agent content-cache command did not exit and close its output streams within {PackagedContentCacheCommandTimeout}.");
            }

            if (ReferenceEquals(completed, processExitMonitor))
            {
                await processExit.ConfigureAwait(false);
                processExited = true;
                processExitMonitor = never;
                continue;
            }

            if (ReferenceEquals(completed, standardOutputMonitor))
            {
                try
                {
                    _ = await standardOutput.ConfigureAwait(false);
                }
                catch
                {
                    await TerminatePackagedContentCacheCommandAsync(process)
                        .ConfigureAwait(false);
                    await ObservePackagedContentCacheCommandTaskAsync(standardError)
                        .ConfigureAwait(false);
                    throw;
                }

                standardOutputCompleted = true;
                standardOutputMonitor = never;
                continue;
            }

            try
            {
                _ = await standardError.ConfigureAwait(false);
            }
            catch
            {
                await TerminatePackagedContentCacheCommandAsync(process)
                    .ConfigureAwait(false);
                await ObservePackagedContentCacheCommandTaskAsync(standardOutput)
                    .ConfigureAwait(false);
                throw;
            }

            standardErrorCompleted = true;
            standardErrorMonitor = never;
        }

        process.WaitForExit();
        return new PackagedContentCacheCommandResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static async Task<string> ReadBoundedPackagedCommandOutputAsync(
        StreamReader reader,
        string streamName)
    {
        var output = new StringBuilder();
        var buffer = new char[2048];
        while (true)
        {
            var charactersRead = await reader.ReadAsync(buffer.AsMemory())
                .ConfigureAwait(false);
            if (charactersRead == 0)
            {
                return output.ToString();
            }

            if (output.Length
                > MaximumPackagedContentCacheCommandOutputCharacters
                - charactersRead)
            {
                throw new InvalidDataException(
                    $"The packaged Station Agent content-cache command exceeded the {MaximumPackagedContentCacheCommandOutputCharacters}-character {streamName} limit.");
            }

            output.Append(buffer, 0, charactersRead);
        }
    }

    private static async Task TerminatePackagedContentCacheCommandAsync(
        Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }

        using var terminationTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(terminationTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (terminationTimeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The packaged Station Agent content-cache command process tree could not be terminated within ten seconds.");
        }
    }

    private static async Task ObservePackagedContentCacheCommandTaskAsync(
        Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException
                                          and not OutOfMemoryException)
        {
        }
    }

    private static void RequireExactPackagedContentCacheCommandResult(
        PackagedContentCacheCommandResult result,
        int expectedExitCode,
        string expectedStandardOutput,
        string expectedStandardError,
        string operation)
    {
        if (result.ExitCode == expectedExitCode
            && string.Equals(
                result.StandardOutput,
                expectedStandardOutput,
                StringComparison.Ordinal)
            && string.Equals(
                result.StandardError,
                expectedStandardError,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidDataException(
            $"The packaged Station Agent failed to {operation} with the exact CLI contract. "
            + $"Expected exit {expectedExitCode}, stdout {JsonSerializer.Serialize(expectedStandardOutput)}, stderr {JsonSerializer.Serialize(expectedStandardError)}; "
            + $"received exit {result.ExitCode}, stdout {JsonSerializer.Serialize(result.StandardOutput)}, stderr {JsonSerializer.Serialize(result.StandardError)}.");
    }

    private static ProtectedPackageState[] EnumerateProtectedPackageStates(
        string packageCacheRoot)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(packageCacheRoot))
        {
            var leaf = Path.GetFileName(directory) ?? string.Empty;
            if (IsCanonicalContentSha256(leaf))
            {
                hashes.Add(leaf);
                continue;
            }

            const string markerSuffix = ".installed";
            if (leaf.Length == 1 + 64 + markerSuffix.Length
                && leaf[0] == '.'
                && leaf.EndsWith(markerSuffix, StringComparison.Ordinal))
            {
                var markerHash = leaf[1..^markerSuffix.Length];
                if (IsCanonicalContentSha256(markerHash))
                {
                    hashes.Add(markerHash);
                }
            }
        }

        return [.. hashes
            .Order(StringComparer.Ordinal)
            .Select(contentSha256 => new ProtectedPackageState(
                contentSha256,
                Directory.Exists(Path.Combine(packageCacheRoot, contentSha256)),
                Directory.Exists(Path.Combine(
                    packageCacheRoot,
                    $".{contentSha256}.installed"))))];
    }

    private static void RequirePackageCacheEntryState(
        string packageCacheRoot,
        string contentSha256,
        bool contentExpected,
        bool markerExpected,
        string scenario)
    {
        var contentExists = Directory.Exists(
            Path.Combine(packageCacheRoot, contentSha256));
        var markerExists = Directory.Exists(
            Path.Combine(packageCacheRoot, $".{contentSha256}.installed"));
        if (contentExists != contentExpected || markerExists != markerExpected)
        {
            throw new InvalidDataException(
                $"The {scenario} cache state for '{contentSha256}' is invalid: "
                + $"content expected={contentExpected} actual={contentExists}, "
                + $"marker expected={markerExpected} actual={markerExists}.");
        }
    }

    private static void RequireCanonicalContentSha256(
        string contentSha256,
        string parameterName)
    {
        if (!IsCanonicalContentSha256(contentSha256))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 value is required.",
                parameterName);
        }
    }

    private static bool IsCanonicalContentSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record PackagedContentCacheCommandContext(
        string ExecutablePath,
        string PackageCacheRoot,
        string StationServiceName,
        ImmutableContentProtectionPolicy Policy);

    private sealed record PackagedContentCacheCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record ProtectedPackageState(
        string ContentSha256,
        bool ContentPresent,
        bool MarkerPresent);
}
