using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace OpenLineOps.Agent.Tests;

internal sealed record RealCoordinatorCredential(
    string CredentialId,
    string ActorId,
    string Token);

internal sealed record RealCoordinatorStationAgentCredential(
    string CredentialId,
    string AgentId,
    string StationId,
    string Token);

internal sealed record RealCoordinatorDeployment(
    string ProjectId,
    string ApplicationId,
    string StationSystemId,
    string AgentId,
    string StationId);

internal sealed record RealCoordinatorProcessCredentials(
    RealCoordinatorCredential Engineering,
    RealCoordinatorCredential Operator,
    RealCoordinatorCredential Safety,
    RealCoordinatorStationAgentCredential FirstStationAgent,
    RealCoordinatorStationAgentCredential SecondStationAgent)
{
    public static RealCoordinatorProcessCredentials CreateRandom(
        RealCoordinatorDeployment firstDeployment,
        RealCoordinatorDeployment secondDeployment)
    {
        ArgumentNullException.ThrowIfNull(firstDeployment);
        ArgumentNullException.ThrowIfNull(secondDeployment);
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return new RealCoordinatorProcessCredentials(
            new RealCoordinatorCredential(
                $"engineering-{suffix}",
                $"engineering.{suffix}",
                CreateToken()),
            new RealCoordinatorCredential(
                $"operator-{suffix}",
                $"operator.{suffix}",
                CreateToken()),
            new RealCoordinatorCredential(
                $"safety-{suffix}",
                $"safety.{suffix}",
                CreateToken()),
            new RealCoordinatorStationAgentCredential(
                $"station-agent-1-{suffix}",
                firstDeployment.AgentId,
                firstDeployment.StationId,
                CreateToken()),
            new RealCoordinatorStationAgentCredential(
                $"station-agent-2-{suffix}",
                secondDeployment.AgentId,
                secondDeployment.StationId,
                CreateToken()));
    }

    private static string CreateToken() => Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(48))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

internal sealed record RealCoordinatorStatePaths(
    string RuntimeDatabasePath,
    string TraceDatabasePath,
    string OperationsDatabasePath,
    string DevicesDatabasePath,
    string PluginEventLogDatabasePath,
    string CentralArtifactRoot)
{
    public static RealCoordinatorStatePaths Under(string workRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);
        var root = Path.GetFullPath(workRoot);
        var stateRoot = Path.Combine(root, "state");
        return new RealCoordinatorStatePaths(
            Path.Combine(stateRoot, "runtime.sqlite"),
            Path.Combine(stateRoot, "trace.sqlite"),
            Path.Combine(stateRoot, "operations.sqlite"),
            Path.Combine(stateRoot, "devices.sqlite"),
            Path.Combine(stateRoot, "plugin-events.sqlite"),
            Path.Combine(root, "central-artifacts"));
    }
}

internal sealed record RealCoordinatorProcessOptions
{
    public required string ApiExecutablePath { get; init; }

    public required string ExpectedApiExecutableSha256 { get; init; }

    public required string WorkRoot { get; init; }

    public required int LoopbackPort { get; init; }

    public required string StartupProjectFile { get; init; }

    public required string PostgreSqlConnectionString { get; init; }

    public required Uri RabbitMqBrokerUri { get; init; }

    public required bool RabbitMqRequireTls { get; init; }

    public required string CoordinatorId { get; init; }

    public required string DeploymentCatalogDirectory { get; init; }

    public required RealCoordinatorDeployment FirstDeployment { get; init; }

    public required RealCoordinatorDeployment SecondDeployment { get; init; }

    public required RealCoordinatorProcessCredentials Credentials { get; init; }

    public required RealCoordinatorStatePaths StatePaths { get; init; }

    public required string PluginHostExecutablePath { get; init; }

    public required string PythonWorkerExecutablePath { get; init; }

    public string PythonWorkerArguments { get; init; } = string.Empty;

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan HttpRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

internal sealed record RealCoordinatorProcessIdentity(
    string ExecutablePath,
    string ExecutableSha256,
    int ProcessId,
    int StartOrdinal,
    DateTimeOffset StartedAtUtc,
    string EnvironmentSha256,
    string PrivateStandardOutputPath,
    string PrivateStandardErrorPath);

[SupportedOSPlatform("windows")]
internal sealed class RealCoordinatorProcessHarness : IAsyncDisposable
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly string[] SafeInheritedEnvironmentVariables =
    [
        "ALLUSERSPROFILE",
        "APPDATA",
        "COMSPEC",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432",
        "DOTNET_ROOT",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT(x86)",
        "LOCALAPPDATA",
        "NUMBER_OF_PROCESSORS",
        "OS",
        "PATH",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL",
        "PROCESSOR_REVISION",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "SystemDrive",
        "SystemRoot",
        "USERDOMAIN",
        "USERNAME",
        "USERPROFILE",
        "WINDIR"
    ];

    private readonly RealCoordinatorProcessOptions _options;
    private readonly string _apiExecutablePath;
    private readonly string _apiExecutableSha256;
    private readonly string _privateLogRoot;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly string _environmentSha256;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly List<RealCoordinatorProcessIdentity> _starts = [];
    private readonly string _harnessId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private Process? _process;
    private Task? _standardOutputPump;
    private Task? _standardErrorPump;
    private bool _disposed;
    private int _startOrdinal;

    private RealCoordinatorProcessHarness(RealCoordinatorProcessOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The real Coordinator process harness requires Windows and OpenLineOps.Api.exe.");
        }

        _options = ValidateOptions(options);
        _apiExecutablePath = CanonicalExistingFile(
            options.ApiExecutablePath,
            nameof(options.ApiExecutablePath));
        if (!string.Equals(
                Path.GetFileName(_apiExecutablePath),
                "OpenLineOps.Api.exe",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "ApiExecutablePath must identify the staged OpenLineOps.Api.exe entrypoint.",
                nameof(options));
        }

        _apiExecutableSha256 = RequireSha256(
            options.ExpectedApiExecutableSha256,
            nameof(options.ExpectedApiExecutableSha256));
        VerifyExecutableHash();
        BaseUri = new Uri(
            $"http://127.0.0.1:{options.LoopbackPort.ToString(CultureInfo.InvariantCulture)}/",
            UriKind.Absolute);
        _privateLogRoot = Path.Combine(options.WorkRoot, "private-coordinator-io");
        Directory.CreateDirectory(_privateLogRoot);
        var privateTempRoot = Path.Combine(options.WorkRoot, "private-temp");
        Directory.CreateDirectory(privateTempRoot);
        CreateStateDirectories(options.StatePaths);
        _environment = BuildEnvironment(options, privateTempRoot);
        _environmentSha256 = HashEnvironment(_environment);
    }

    public Uri BaseUri { get; }

    public RealCoordinatorProcessCredentials Credentials => _options.Credentials;

    public RealCoordinatorStatePaths StatePaths => _options.StatePaths;

    public RealCoordinatorProcessIdentity CurrentIdentity => _process is not null
        ? _starts[^1]
        : throw new InvalidOperationException("The real Coordinator process is not running.");

    public IReadOnlyList<RealCoordinatorProcessIdentity> StartHistory => _starts.AsReadOnly();

    public bool IsRunning => _process is { HasExited: false };

    public static string ComputeFileSha256(string path)
    {
        var filePath = CanonicalExistingFile(path, nameof(path));
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static async Task<RealCoordinatorProcessHarness> StartAsync(
        RealCoordinatorProcessOptions options,
        CancellationToken cancellationToken = default)
    {
        var harness = new RealCoordinatorProcessHarness(options);
        try
        {
            await harness.StartProcessAsync(cancellationToken).ConfigureAwait(false);
            return harness;
        }
        catch
        {
            await harness.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public HttpClient CreateBearerClient(string token)
    {
        ThrowIfDisposed();
        RequireToken(token, nameof(token));
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            UseCookies = false
        })
        {
            BaseAddress = BaseUri,
            Timeout = _options.HttpRequestTimeout
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token);
        return client;
    }

    public HttpClient CreateEngineeringClient() =>
        CreateBearerClient(Credentials.Engineering.Token);

    public HttpClient CreateOperatorClient() =>
        CreateBearerClient(Credentials.Operator.Token);

    public HttpClient CreateSafetyClient() =>
        CreateBearerClient(Credentials.Safety.Token);

    public HttpClient CreateFirstStationAgentClient() =>
        CreateBearerClient(Credentials.FirstStationAgent.Token);

    public HttpClient CreateSecondStationAgentClient() =>
        CreateBearerClient(Credentials.SecondStationAgent.Token);

    // Restarts only after the previous process tree has exited; the immutable
    // environment fingerprint must therefore remain identical across starts.
    public async Task<RealCoordinatorProcessIdentity> RestartAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_process is not null)
            {
                await TerminateCurrentProcessTreeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return await StartCurrentProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // This is a bounded process-tree stop, not evidence of cooperative host shutdown.
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await TerminateCurrentProcessTreeAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // The separately named crash operation keeps recovery scenarios explicit.
    public async Task CrashAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await TerminateCurrentProcessTreeAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_process is not null)
                {
                    await TerminateCurrentProcessTreeAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                _disposed = true;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartProcessAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await StartCurrentProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<RealCoordinatorProcessIdentity> StartCurrentProcessAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_process is not null)
        {
            throw new InvalidOperationException("The real Coordinator process is already running.");
        }

        VerifyExecutableHash();
        VerifyLoopbackPortAvailable(_options.LoopbackPort);
        var ordinal = checked(++_startOrdinal);
        var stdoutPath = Path.Combine(
            _privateLogRoot,
            $"coordinator-{_harnessId}-{ordinal:D2}.stdout.log");
        var stderrPath = Path.Combine(
            _privateLogRoot,
            $"coordinator-{_harnessId}-{ordinal:D2}.stderr.log");
        var startInfo = new ProcessStartInfo
        {
            FileName = _apiExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(_apiExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        startInfo.Environment.Clear();
        foreach (var variable in _environment)
        {
            startInfo.Environment.Add(variable.Key, variable.Value);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned no OpenLineOps.Api process.");
        _process = process;
        _standardOutputPump = PumpAsync(process.StandardOutput, stdoutPath);
        _standardErrorPump = PumpAsync(process.StandardError, stderrPath);
        try
        {
            var identity = CaptureProcessIdentity(process, ordinal, stdoutPath, stderrPath);
            _starts.Add(identity);
            await WaitUntilReadyAsync(process, cancellationToken).ConfigureAwait(false);
            return identity;
        }
        catch
        {
            await TerminateCurrentProcessTreeAsync(CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private RealCoordinatorProcessIdentity CaptureProcessIdentity(
        Process process,
        int ordinal,
        string stdoutPath,
        string stderrPath)
    {
        if (process.Id <= 0
            || process.Id == Environment.ProcessId
            || _starts.Any(start => start.ProcessId == process.Id))
        {
            throw new InvalidOperationException("Process.Start returned an invalid or reused process id.");
        }

        var actualPath = ReadRequiredExecutablePath(process);
        if (!PathComparer.Equals(actualPath, _apiExecutablePath))
        {
            throw new InvalidOperationException(
                "The started process image is not the validated OpenLineOps.Api executable.");
        }

        var actualSha256 = ComputeFileSha256(actualPath);
        if (!string.Equals(actualSha256, _apiExecutableSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The started OpenLineOps.Api executable hash changed before identity capture.");
        }

        return new RealCoordinatorProcessIdentity(
            actualPath,
            actualSha256,
            process.Id,
            ordinal,
            DateTimeOffset.UtcNow,
            _environmentSha256,
            stdoutPath,
            stderrPath);
    }

    private static string ReadRequiredExecutablePath(Process process)
    {
        const int maximumWindowsPathLength = 32_768;
        var executablePath = new StringBuilder(maximumWindowsPathLength);
        var executablePathLength = checked((uint)executablePath.Capacity);
        if (!QueryFullProcessImageName(
                process.Handle,
                flags: 0,
                executablePath,
                ref executablePathLength))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not inspect the started Coordinator process image path.");
        }

        if (executablePathLength == 0)
        {
            throw new InvalidOperationException(
                "The started Coordinator process image path is empty.");
        }

        return Path.GetFullPath(
            executablePath.ToString(0, checked((int)executablePathLength)));
    }

    private async Task WaitUntilReadyAsync(Process process, CancellationToken cancellationToken)
    {
        using var client = CreateOperatorClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"OpenLineOps.Api exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)} before /health/ready. "
                    + $"Private diagnostics: '{CurrentIdentity.PrivateStandardOutputPath}' and "
                    + $"'{CurrentIdentity.PrivateStandardErrorPath}'.");
            }

            try
            {
                using var response = await client.GetAsync(
                        "health/ready",
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException(
                        "OpenLineOps.Api rejected the configured Operator bearer credential during readiness.");
                }
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
                // Kestrel or an external readiness dependency is still starting.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task TerminateCurrentProcessTreeAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Kill(entireProcessTree: true);
                _ = await WaitForExitAsync(
                        process,
                        _options.ShutdownTimeout,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                await CompletePumpsAsync().ConfigureAwait(false);
            }
            finally
            {
                process.Dispose();
                _process = null;
                _standardOutputPump = null;
                _standardErrorPump = null;
            }
        }
    }

    private async Task CompletePumpsAsync()
    {
        var pumps = new[] { _standardOutputPump, _standardErrorPump }
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (pumps.Length != 0)
        {
            await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task PumpAsync(StreamReader reader, string path)
    {
        await using var output = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await reader.BaseStream.CopyToAsync(output).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static RealCoordinatorProcessOptions ValidateOptions(
        RealCoordinatorProcessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = CanonicalAbsoluteDirectory(options.WorkRoot, nameof(options.WorkRoot), create: true);
        _ = CanonicalExistingFile(options.StartupProjectFile, nameof(options.StartupProjectFile));
        if (!string.Equals(
                Path.GetExtension(options.StartupProjectFile),
                ".oloproj",
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "StartupProjectFile must identify one .oloproj project manifest.",
                nameof(options));
        }

        if (options.LoopbackPort is <= IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "LoopbackPort must be an explicit non-zero TCP port.");
        }

        RequireCanonicalText(
            options.PostgreSqlConnectionString,
            nameof(options.PostgreSqlConnectionString));
        ValidateRabbitMq(options.RabbitMqBrokerUri, options.RabbitMqRequireTls);
        RequireCanonicalIdentifier(options.CoordinatorId, nameof(options.CoordinatorId));
        _ = CanonicalAbsoluteDirectory(
            options.DeploymentCatalogDirectory,
            nameof(options.DeploymentCatalogDirectory),
            create: false);
        _ = CanonicalExistingFile(
            options.PluginHostExecutablePath,
            nameof(options.PluginHostExecutablePath));
        _ = CanonicalExistingFile(
            options.PythonWorkerExecutablePath,
            nameof(options.PythonWorkerExecutablePath));
        ValidateDuration(options.StartupTimeout, nameof(options.StartupTimeout));
        ValidateDuration(options.ShutdownTimeout, nameof(options.ShutdownTimeout));
        ValidateDuration(options.HttpRequestTimeout, nameof(options.HttpRequestTimeout));

        ArgumentNullException.ThrowIfNull(options.FirstDeployment);
        ArgumentNullException.ThrowIfNull(options.SecondDeployment);
        ValidateDeployment(options.FirstDeployment, nameof(options.FirstDeployment));
        ValidateDeployment(options.SecondDeployment, nameof(options.SecondDeployment));
        if (Equals(options.FirstDeployment, options.SecondDeployment)
            || string.Equals(
                options.FirstDeployment.StationSystemId,
                options.SecondDeployment.StationSystemId,
                StringComparison.Ordinal)
            || (string.Equals(
                    options.FirstDeployment.AgentId,
                    options.SecondDeployment.AgentId,
                    StringComparison.Ordinal)
                && string.Equals(
                    options.FirstDeployment.StationId,
                    options.SecondDeployment.StationId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The real Coordinator harness requires two distinct Station deployments.",
                nameof(options));
        }

        ArgumentNullException.ThrowIfNull(options.Credentials);
        ValidateCredentials(
            options.Credentials,
            options.FirstDeployment,
            options.SecondDeployment);
        ArgumentNullException.ThrowIfNull(options.StatePaths);
        ValidateStatePaths(options.WorkRoot, options.StatePaths);
        return options;
    }

    private static void ValidateDeployment(RealCoordinatorDeployment deployment, string name)
    {
        RequireCanonicalText(deployment.ProjectId, $"{name}.ProjectId");
        RequireCanonicalText(deployment.ApplicationId, $"{name}.ApplicationId");
        RequireCanonicalText(deployment.StationSystemId, $"{name}.StationSystemId");
        RequireStationIdentity(deployment.AgentId, $"{name}.AgentId");
        RequireStationIdentity(deployment.StationId, $"{name}.StationId");
    }

    private static void ValidateCredentials(
        RealCoordinatorProcessCredentials credentials,
        RealCoordinatorDeployment firstDeployment,
        RealCoordinatorDeployment secondDeployment)
    {
        ArgumentNullException.ThrowIfNull(credentials.Engineering);
        ArgumentNullException.ThrowIfNull(credentials.Operator);
        ArgumentNullException.ThrowIfNull(credentials.Safety);
        ArgumentNullException.ThrowIfNull(credentials.FirstStationAgent);
        ArgumentNullException.ThrowIfNull(credentials.SecondStationAgent);
        ValidateCredential(credentials.Engineering, nameof(credentials.Engineering));
        ValidateCredential(credentials.Operator, nameof(credentials.Operator));
        ValidateCredential(credentials.Safety, nameof(credentials.Safety));
        ValidateStationCredential(
            credentials.FirstStationAgent,
            firstDeployment,
            nameof(credentials.FirstStationAgent));
        ValidateStationCredential(
            credentials.SecondStationAgent,
            secondDeployment,
            nameof(credentials.SecondStationAgent));

        var credentialIds = new[]
        {
            credentials.Engineering.CredentialId,
            credentials.Operator.CredentialId,
            credentials.Safety.CredentialId,
            credentials.FirstStationAgent.CredentialId,
            credentials.SecondStationAgent.CredentialId
        };
        var tokens = new[]
        {
            credentials.Engineering.Token,
            credentials.Operator.Token,
            credentials.Safety.Token,
            credentials.FirstStationAgent.Token,
            credentials.SecondStationAgent.Token
        };
        if (credentialIds.Distinct(StringComparer.Ordinal).Count() != credentialIds.Length
            || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length)
        {
            throw new ArgumentException(
                "Engineering, Operator, Safety, and both Station Agents require distinct credentials.",
                nameof(credentials));
        }
    }

    private static void ValidateCredential(RealCoordinatorCredential credential, string name)
    {
        RequireCanonicalIdentifier(credential.CredentialId, $"{name}.CredentialId");
        RequireCanonicalIdentifier(credential.ActorId, $"{name}.ActorId");
        RequireToken(credential.Token, $"{name}.Token");
    }

    private static void ValidateStationCredential(
        RealCoordinatorStationAgentCredential credential,
        RealCoordinatorDeployment deployment,
        string name)
    {
        RequireCanonicalIdentifier(credential.CredentialId, $"{name}.CredentialId");
        RequireStationIdentity(credential.AgentId, $"{name}.AgentId");
        RequireStationIdentity(credential.StationId, $"{name}.StationId");
        RequireToken(credential.Token, $"{name}.Token");
        if (!string.Equals(credential.AgentId, deployment.AgentId, StringComparison.Ordinal)
            || !string.Equals(credential.StationId, deployment.StationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{name} must match its configured Station deployment identity.",
                name);
        }
    }

    private static void ValidateStatePaths(string workRoot, RealCoordinatorStatePaths statePaths)
    {
        var paths = new[]
        {
            statePaths.RuntimeDatabasePath,
            statePaths.TraceDatabasePath,
            statePaths.OperationsDatabasePath,
            statePaths.DevicesDatabasePath,
            statePaths.PluginEventLogDatabasePath
        };
        foreach (var path in paths)
        {
            _ = CanonicalAbsolutePath(path, nameof(statePaths));
            EnsureUnderRoot(path, workRoot, nameof(statePaths));
        }

        if (paths.Distinct(PathComparer).Count() != paths.Length)
        {
            throw new ArgumentException("Every SQLite store requires a distinct database path.", nameof(statePaths));
        }

        _ = CanonicalAbsolutePath(statePaths.CentralArtifactRoot, nameof(statePaths));
        EnsureUnderRoot(statePaths.CentralArtifactRoot, workRoot, nameof(statePaths));
    }

    private static void CreateStateDirectories(RealCoordinatorStatePaths statePaths)
    {
        foreach (var databasePath in new[]
                 {
                     statePaths.RuntimeDatabasePath,
                     statePaths.TraceDatabasePath,
                     statePaths.OperationsDatabasePath,
                     statePaths.DevicesDatabasePath,
                     statePaths.PluginEventLogDatabasePath
                 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        }

        Directory.CreateDirectory(statePaths.CentralArtifactRoot);
    }

    private static ReadOnlyDictionary<string, string> BuildEnvironment(
        RealCoordinatorProcessOptions options,
        string privateTempRoot)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SafeInheritedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                environment[name] = value;
            }
        }

        environment["TEMP"] = privateTempRoot;
        environment["TMP"] = privateTempRoot;
        environment["DOTNET_ENVIRONMENT"] = "Production";
        environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        environment["ASPNETCORE_URLS"] =
            $"http://127.0.0.1:{options.LoopbackPort.ToString(CultureInfo.InvariantCulture)}";
        environment["ASPNETCORE_PREVENTHOSTINGSTARTUP"] = "true";
        environment["ASPNETCORE_SUPPRESSSTATUSMESSAGES"] = "true";
        environment["AllowedHosts"] = "127.0.0.1";
        environment["OpenLineOps__Desktop__AllowedOrigins__0"] =
            $"http://127.0.0.1:{options.LoopbackPort.ToString(CultureInfo.InvariantCulture)}";
        environment["OpenLineOps__Projects__StartupWorkspaces__ProjectFiles__0"] =
            options.StartupProjectFile;
        environment["OpenLineOps__Runtime__Persistence__Provider"] = "Sqlite";
        environment["OpenLineOps__Runtime__Persistence__DatabasePath"] =
            options.StatePaths.RuntimeDatabasePath;
        environment["OpenLineOps__Runtime__Coordination__Provider"] = "PostgreSql";
        environment["OpenLineOps__Runtime__Coordination__ConnectionString"] =
            options.PostgreSqlConnectionString;
        environment["OpenLineOps__Runtime__AgentTransport__Provider"] = "RabbitMq";
        environment["OpenLineOps__Runtime__AgentTransport__BrokerUri"] =
            options.RabbitMqBrokerUri.AbsoluteUri;
        environment["OpenLineOps__Runtime__AgentTransport__RequireTls"] =
            options.RabbitMqRequireTls ? "true" : "false";
        environment["OpenLineOps__Runtime__AgentTransport__CoordinatorId"] =
            options.CoordinatorId;
        environment["OpenLineOps__Runtime__AgentTransport__DeploymentCatalogDirectory"] =
            options.DeploymentCatalogDirectory;
        environment["OpenLineOps__Runtime__StationExecution__Provider"] = "Agent";
        environment["OpenLineOps__Runtime__AgentPresence__TimeToLive"] = "00:00:15";
        environment["OpenLineOps__Runtime__Scripting__Python__ExecutionMode"] = "ProcessIsolated";
        environment["OpenLineOps__Runtime__Scripting__Python__WorkerFileName"] =
            options.PythonWorkerExecutablePath;
        environment["OpenLineOps__Runtime__Scripting__Python__WorkerArguments"] =
            options.PythonWorkerArguments;
        environment["OpenLineOps__Runtime__Scripting__Python__WorkerWorkingDirectory"] =
            Path.GetDirectoryName(options.PythonWorkerExecutablePath)!;
        environment["OpenLineOps__Runtime__Scripting__Python__Sandbox__IsolationMode"] =
            "ExternalProcess";
        environment["OpenLineOps__Runtime__Scripting__Python__Sandbox__RequireLeastPrivilegeExecution"] =
            "false";
        environment["OpenLineOps__Traceability__Persistence__Provider"] = "Sqlite";
        environment["OpenLineOps__Traceability__Persistence__DatabasePath"] =
            options.StatePaths.TraceDatabasePath;
        environment["OpenLineOps__Traceability__ProjectionRebuild__Enabled"] = "true";
        environment["OpenLineOps__Traceability__ArtifactStorage__Provider"] = "FileSystem";
        environment["OpenLineOps__Traceability__ArtifactStorage__RootPath"] =
            options.StatePaths.CentralArtifactRoot;
        environment["OpenLineOps__Traceability__ArtifactUpload__MaximumArtifactSizeBytes"] =
            "268435456";
        environment["OpenLineOps__Operations__Persistence__Provider"] = "Sqlite";
        environment["OpenLineOps__Operations__Persistence__DatabasePath"] =
            options.StatePaths.OperationsDatabasePath;
        environment["OpenLineOps__Devices__Persistence__Provider"] = "Sqlite";
        environment["OpenLineOps__Devices__Persistence__DatabasePath"] =
            options.StatePaths.DevicesDatabasePath;
        environment["OpenLineOps__Plugins__EventLog__Provider"] = "Sqlite";
        environment["OpenLineOps__Plugins__EventLog__DatabasePath"] =
            options.StatePaths.PluginEventLogDatabasePath;
        environment["OpenLineOps__Plugins__ExternalHost__ExecutablePath"] =
            options.PluginHostExecutablePath;
        environment["OpenLineOps__Plugins__ExternalHost__Sandbox__IsolationMode"] =
            "ExternalProcess";

        SetDeployment(environment, 0, options.FirstDeployment);
        SetDeployment(environment, 1, options.SecondDeployment);
        SetCaller(environment, 0, options.Credentials.Engineering, "Engineering");
        SetCaller(environment, 1, options.Credentials.Operator, "Operator");
        SetCaller(environment, 2, options.Credentials.Safety, "Safety");
        SetStationCaller(environment, 3, options.Credentials.FirstStationAgent);
        SetStationCaller(environment, 4, options.Credentials.SecondStationAgent);
        return new ReadOnlyDictionary<string, string>(environment);
    }

    private static void SetDeployment(
        Dictionary<string, string> environment,
        int index,
        RealCoordinatorDeployment deployment)
    {
        var prefix = $"OpenLineOps__Runtime__AgentTransport__Deployments__{index}";
        environment[$"{prefix}__ProjectId"] = deployment.ProjectId;
        environment[$"{prefix}__ApplicationId"] = deployment.ApplicationId;
        environment[$"{prefix}__StationSystemId"] = deployment.StationSystemId;
        environment[$"{prefix}__AgentId"] = deployment.AgentId;
        environment[$"{prefix}__StationId"] = deployment.StationId;
    }

    private static void SetCaller(
        Dictionary<string, string> environment,
        int index,
        RealCoordinatorCredential credential,
        string role)
    {
        SetCaller(
            environment,
            index,
            credential.CredentialId,
            credential.ActorId,
            credential.Token,
            role,
            stationId: null);
    }

    private static void SetStationCaller(
        Dictionary<string, string> environment,
        int index,
        RealCoordinatorStationAgentCredential credential)
    {
        SetCaller(
            environment,
            index,
            credential.CredentialId,
            credential.AgentId,
            credential.Token,
            "StationAgent",
            credential.StationId);
    }

    private static void SetCaller(
        Dictionary<string, string> environment,
        int index,
        string credentialId,
        string actorId,
        string token,
        string role,
        string? stationId)
    {
        var prefix = $"OpenLineOps__Security__Callers__{index}";
        environment[$"{prefix}__CredentialId"] = credentialId;
        environment[$"{prefix}__ActorId"] = actorId;
        environment[$"{prefix}__TokenSha256"] = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        environment[$"{prefix}__Roles__0"] = role;
        if (stationId is not null)
        {
            environment[$"{prefix}__StationId"] = stationId;
        }
    }

    private static string HashEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var canonical = string.Join(
            '\n',
            environment
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private void VerifyExecutableHash()
    {
        var actual = ComputeFileSha256(_apiExecutablePath);
        if (!string.Equals(actual, _apiExecutableSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "OpenLineOps.Api executable SHA-256 does not match the staged candidate hash.");
        }
    }

    private static void VerifyLoopbackPortAvailable(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                $"Loopback port {port.ToString(CultureInfo.InvariantCulture)} is unavailable.",
                exception);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void ValidateRabbitMq(Uri brokerUri, bool requireTls)
    {
        ArgumentNullException.ThrowIfNull(brokerUri);
        if (!brokerUri.IsAbsoluteUri || brokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new ArgumentException("RabbitMqBrokerUri must be an absolute amqp or amqps URI.");
        }

        if (requireTls && !string.Equals(brokerUri.Scheme, "amqps", StringComparison.Ordinal))
        {
            throw new ArgumentException("RabbitMqRequireTls requires an amqps broker URI.");
        }

        if (!requireTls
            && string.Equals(brokerUri.Scheme, "amqp", StringComparison.Ordinal)
            && !IsLoopbackHost(brokerUri.Host))
        {
            throw new ArgumentException("Cleartext RabbitMQ is allowed only on loopback in this harness.");
        }
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static void ValidateDuration(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(name, "Duration must be between zero and ten minutes.");
        }
    }

    private static string CanonicalExistingFile(string value, string name)
    {
        var path = CanonicalAbsolutePath(value, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configured {name} does not exist.", path);
        }

        return path;
    }

    private static string CanonicalAbsoluteDirectory(string value, string name, bool create)
    {
        var path = CanonicalAbsolutePath(value, name);
        if (create)
        {
            Directory.CreateDirectory(path);
        }
        else if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Configured {name} does not exist: '{path}'.");
        }

        return path;
    }

    private static string CanonicalAbsolutePath(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"{name} must be one canonical absolute path.", name);
        }

        var path = Path.GetFullPath(value);
        if (!PathComparer.Equals(path, value))
        {
            throw new ArgumentException($"{name} must already be normalized.", name);
        }

        var root = Path.GetPathRoot(path);
        if (PathComparer.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar)))
        {
            throw new ArgumentException($"{name} cannot be a filesystem root.", name);
        }

        return path;
    }

    private static void EnsureUnderRoot(string path, string root, string name)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative)
            || string.Equals(relative, "..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must remain beneath WorkRoot.", name);
        }
    }

    private static string RequireSha256(string value, string name) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ArgumentException($"{name} must be one lowercase SHA-256 digest.", name);

    private static void RequireCanonicalText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must be canonical non-empty text.", name);
        }
    }

    private static void RequireCanonicalIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '.' or '_' or ':' or '@' or '/' or '-')))
        {
            throw new ArgumentException($"{name} is not a canonical caller identifier.", name);
        }
    }

    private static void RequireStationIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 128
            || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character)
                  || character is '.' or '_' or ':' or '@' or '-')))
        {
            throw new ArgumentException($"{name} is not a canonical Station identity.", name);
        }
    }

    private static void RequireToken(string token, string name)
    {
        if (token.Length is < 43 or > 86
            || token.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException($"{name} is not a canonical high-entropy bearer token.", name);
        }

        var padded = token.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length is < 32 or > 64
                || !string.Equals(
                    Convert.ToBase64String(bytes)
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_'),
                    token,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{name} is not a canonical high-entropy bearer token.",
                    name);
            }
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                $"{name} is not a canonical high-entropy bearer token.",
                name,
                exception);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "QueryFullProcessImageNameW writes the image path into a caller-owned buffer.")]
    private static extern bool QueryFullProcessImageName(
        IntPtr processHandle,
        uint flags,
        StringBuilder executablePath,
        ref uint executablePathLength);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
