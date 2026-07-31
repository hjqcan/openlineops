using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentExecutableContractTests
{
    [Fact]
    public void ProcessFailureDiagnosticIsSingleLineAndNeverEmitsAStackOrSourcePath()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("Deterministic process failure.");
        }
        catch (Exception caught)
        {
            exception = caught;
        }

        var diagnostic = StationAgentDiagnostics.FormatFailureMessage(
            "OpenLineOps Station Agent terminated",
            exception);

        Assert.Equal(
            "OpenLineOps Station Agent terminated: System.InvalidOperationException: Deterministic process failure.",
            diagnostic);
        Assert.DoesNotContain(" at ", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticFormatterRedactsCredentialsAuthorizationAndBoundsEveryPayload()
    {
        const string brokerUri =
            "amqps://diagnostic-user:diagnostic-password@rabbitmq.local:5671/production";
        const string authorizationSecret = "startup-authorization-secret";
        var exception = new InvalidOperationException(
            $"BrokerUri={brokerUri}; Authorization: Bearer {authorizationSecret}\r\n"
            + new string('x', 5_000));

        var diagnostic = StationAgentDiagnostics.FormatFailureMessage(
            "OpenLineOps Station Agent startup failed",
            exception);

        Assert.Equal(4_096, diagnostic.Length);
        Assert.Contains("InvalidOperationException", diagnostic, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-user", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic-password", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(authorizationSecret, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', diagnostic);
        Assert.DoesNotContain('\n', diagnostic);
    }

    [Fact]
    public void AgentLoggingBoundarySanitizesExceptionAndMessageCredentials()
    {
        const string brokerUri =
            "amqps://logger-user:logger-password@rabbitmq.local:5671/production";
        const string authorizationSecret = "logger-authorization-secret";
        var sink = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(sink);
        });
        StationAgentDiagnostics.ProtectLoggingProviders(services);

        using var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("OpenLineOps.Agent.Diagnostics");
        logger.Log(
            LogLevel.Error,
            new EventId(1201, "AgentDiagnosticFailure"),
            $"Station Agent failed for BrokerUri={brokerUri}",
            new InvalidOperationException(
                $"BrokerUri={brokerUri}; Authorization=Basic {authorizationSecret}"),
            static (message, _) => message);

        Assert.NotNull(sink.Message);
        Assert.Contains("[REDACTED]", sink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("logger-user", sink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("logger-password", sink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(authorizationSecret, sink.Message, StringComparison.Ordinal);
        Assert.Null(sink.Exception);

        using (logger.BeginScope(
                   $"BrokerUri={brokerUri}; Authorization: Bearer {authorizationSecret}"))
        {
            logger.Log(
                LogLevel.Information,
                new EventId(1203, "AgentDiagnosticScope"),
                "Transport scope diagnostic",
                exception: null,
                static (message, _) => message);
        }

        Assert.NotNull(sink.Scope);
        Assert.Contains("[REDACTED]", sink.Scope, StringComparison.Ordinal);
        Assert.DoesNotContain("logger-user", sink.Scope, StringComparison.Ordinal);
        Assert.DoesNotContain("logger-password", sink.Scope, StringComparison.Ordinal);
        Assert.DoesNotContain(authorizationSecret, sink.Scope, StringComparison.Ordinal);

        logger.Log(
            LogLevel.Information,
            new EventId(1202, "AgentDiagnosticMessage"),
            $"Transport status BrokerUri={brokerUri}; Authorization: Bearer {authorizationSecret}",
            exception: null,
            static (message, _) => message);

        Assert.NotNull(sink.Message);
        Assert.Contains("[REDACTED]", sink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("logger-user", sink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("logger-password", sink.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(authorizationSecret, sink.Message, StringComparison.Ordinal);
        Assert.Null(sink.Exception);
    }

    [Fact]
    public void AgentLoggingBoundaryNeverForwardsStructuredStateOrReinvokesFormatter()
    {
        const string structuredSecret = "structured-state-secret";
        var sink = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(sink);
        });
        StationAgentDiagnostics.ProtectLoggingProviders(services);

        using var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("OpenLineOps.Agent.Diagnostics.Structured");
        var formatterCalls = 0;
        var structuredState = new Dictionary<string, string>
        {
            ["safe"] = "Safe diagnostic",
            ["secret"] = structuredSecret
        };

        logger.Log(
            LogLevel.Information,
            new EventId(1204, "StructuredBoundary"),
            structuredState,
            exception: null,
            (state, _) =>
            {
                formatterCalls++;
                return formatterCalls == 1
                    ? state["safe"]
                    : state["secret"];
            });

        Assert.Equal(1, formatterCalls);
        Assert.Equal("Safe diagnostic", sink.Message);
        Assert.IsType<string>(sink.State);
        Assert.DoesNotContain(
            structuredSecret,
            sink.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndeployedReleaseTemplateFailsClosedWithoutAnUnhandledCrashProcess()
    {
        var result = await RunAgentAsync();

        Assert.Equal(70, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            "OpenLineOps:WindowsServiceName must contain 1-80 ASCII",
            result.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministrativeContentCacheModesAreExposedByAgentExecutable()
    {
        var duplicateProvision = await RunAgentAsync(
            StationAgentCommandLine.ProvisionContentCacheSwitch,
            StationAgentCommandLine.ProvisionContentCacheSwitch);
        Assert.Equal(70, duplicateProvision.ExitCode);
        Assert.Empty(duplicateProvision.StandardOutput);
        Assert.Contains(
            "--provision-content-cache may be specified only once",
            duplicateProvision.StandardError,
            StringComparison.Ordinal);

        var invalidRemoval = await RunAgentAsync(
            StationAgentCommandLine.RemoveContentCachePackageSwitch,
            new string('A', 64));
        Assert.Equal(70, invalidRemoval.ExitCode);
        Assert.Empty(invalidRemoval.StandardOutput);
        Assert.Contains(
            "--remove-content-cache-package requires one lowercase SHA-256 value",
            invalidRemoval.StandardError,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisioningModeClassifiesCallerWithoutGenericTokenAccessFailure()
    {
        var result = await RunAgentAsync(
            StationAgentCommandLine.ProvisionContentCacheSwitch);

        Assert.Equal(70, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(
            IsCurrentAdministrativeWindowsCaller()
                ? "OpenLineOps:WindowsServiceName must contain 1-80 ASCII"
                : "requires an elevated Windows administrator or LocalSystem process",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Access is denied",
            result.StandardError,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AgentProcessResult> RunAgentAsync(params string[] arguments)
    {
        var executableDirectory = AgentExecutableDirectory();
        var executablePath = Path.Combine(
            executableDirectory,
            OperatingSystem.IsWindows() ? "OpenLineOps.Agent.exe" : "OpenLineOps.Agent");
        Assert.True(File.Exists(executablePath), $"Station Agent executable is missing: {executablePath}");

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = executableDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var name in startInfo.Environment.Keys.Where(name =>
                     name.StartsWith("OpenLineOps__Agent__", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(
                         name,
                         "OpenLineOps__WindowsServiceName",
                         StringComparison.OrdinalIgnoreCase)
                     || string.Equals(name, "DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(name, "ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Station Agent process could not be started.");
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Station Agent contract probe did not fail closed within ten seconds.");
        }

        return new AgentProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    private static string AgentExecutableDirectory()
    {
        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "OpenLineOps.slnx")))
        {
            directory = directory.Parent;
        }

        var repositoryRoot = directory?.FullName
            ?? throw new DirectoryNotFoundException(
                "OpenLineOps repository root could not be found.");
        return Path.Combine(
            repositoryRoot,
            "src",
            "OpenLineOps.Agent",
            "bin",
            configuration,
            "net10.0");
    }

    private static bool IsCurrentAdministrativeWindowsCaller()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true
               || new WindowsPrincipal(identity).IsInRole(
                   WindowsBuiltInRole.Administrator);
    }

    private sealed record AgentProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class CapturingLoggerProvider :
        ILoggerProvider,
        ISupportExternalScope
    {
        private IExternalScopeProvider? _scopeProvider;

        public string? Message { get; private set; }

        public Exception? Exception { get; private set; }

        public string? Scope { get; private set; }

        public object? State { get; private set; }

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
            _scopeProvider = scopeProvider;

        public void Dispose()
        {
        }

        private void CaptureScopes()
        {
            _scopeProvider?.ForEachScope(
                static (scope, provider) =>
                    provider.Scope = scope?.ToString(),
                this);
        }

        private sealed class CapturingLogger(
            CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                NoopScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                provider.State = state;
                provider.Message = formatter(state, exception);
                provider.Exception = exception;
                provider.CaptureScopes();
            }
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
