using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentHostOptionsTests : IDisposable
{
    private static readonly string ArtifactUploadToken = Convert.ToBase64String(
            SHA256.HashData("station-agent-host-options"u8))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    private readonly string _tempDirectory;
    private readonly string _trustedKeyPath;
    private readonly string _pythonRuntimePath;
    private readonly string _safetyExecutablePath;

    public StationAgentHostOptionsTests()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-agent-host-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _trustedKeyPath = Path.Combine(_tempDirectory, "release-public-key.pem");
        _pythonRuntimePath = Path.Combine(_tempDirectory, "python-runtime.dll");
        _safetyExecutablePath = Path.Combine(_tempDirectory, "station-safety.exe");
        File.WriteAllText(_trustedKeyPath, "test public key");
        File.WriteAllText(_pythonRuntimePath, "test Python runtime");
        File.WriteAllText(_safetyExecutablePath, "independent safety actuator");
    }

    [Fact]
    public void LoadPinsRuntimePluginHostAndPythonWorkerToBundleRoot()
    {
        var options = StationAgentHostOptions.Load(CreateConfiguration());

        Assert.Equal(
            Path.Combine(
                AppContext.BaseDirectory,
                StationAgentHostOptions.RuntimeExecutableFileName),
            options.RuntimeExecutablePath);
        Assert.Equal(
            Path.Combine(
                AppContext.BaseDirectory,
                StationAgentHostOptions.PluginHostExecutableFileName),
            options.PluginHostExecutablePath);
        Assert.Equal(
            Path.Combine(
                AppContext.BaseDirectory,
                StationAgentHostOptions.PythonScriptWorkerExecutableFileName),
            options.PythonScript.WorkerExecutablePath);
        Assert.Equal(
            Path.Combine(
                AppContext.BaseDirectory,
                StationAgentHostOptions.LeastPrivilegeLauncherExecutableFileName),
            options.PythonScript.Sandbox.LeastPrivilegeLauncherExecutable);
        Assert.Equal(
            StationAgentHostOptions.LeastPrivilegeIdentity,
            options.PythonScript.Sandbox.LeastPrivilegeIdentity);
        Assert.True(options.PythonScript.Sandbox.RequireLeastPrivilegeExecution);
        Assert.Equal("station-system-1", options.StationSystemId);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HeartbeatInterval);
        Assert.Equal(
            Path.Combine(_tempDirectory, "content-anchor", "content"),
            options.PackageCacheDirectory);
    }

    [Fact]
    public void LoadRejectsMissingPackageCacheDirectory()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:PackageCacheDirectory"] = null
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            "OpenLineOps:Agent:PackageCacheDirectory is required",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsRelativePackageCacheDirectory()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:PackageCacheDirectory"] = "content-cache"
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            "fully-qualified canonical path",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OpenLineOps:Agent:DataDirectory")]
    [InlineData("OpenLineOps:Agent:PackageDistributionDirectory")]
    [InlineData("OpenLineOps:Agent:RuntimeWorkingDirectory")]
    [InlineData("OpenLineOps:Agent:ArtifactDirectory")]
    [InlineData("OpenLineOps:Agent:SafetyWorkingDirectory")]
    public void LoadRejectsMutableRootInsideDedicatedPackageCacheAnchor(string settingName)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            [settingName] = Path.Combine(_tempDirectory, "content-anchor", "mutable")
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            "must be outside the dedicated PackageCacheDirectory namespace anchor",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsMutableRootContainingDedicatedPackageCacheAnchor()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:DataDirectory"] = _tempDirectory
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            "must be outside the dedicated PackageCacheDirectory namespace anchor",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OpenLineOps:Agent:DataDirectory")]
    [InlineData("OpenLineOps:Agent:PackageDistributionDirectory")]
    [InlineData("OpenLineOps:Agent:RuntimeWorkingDirectory")]
    [InlineData("OpenLineOps:Agent:ArtifactDirectory")]
    [InlineData("OpenLineOps:Agent:SafetyWorkingDirectory")]
    public void LoadRejectsMutableVolumeRootContainingDedicatedPackageCacheAnchor(
        string settingName)
    {
        var volumeRoot = Path.GetPathRoot(_tempDirectory)
            ?? throw new InvalidOperationException("Test temp directory has no volume root.");
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            [settingName] = volumeRoot
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            "must be outside the dedicated PackageCacheDirectory namespace anchor",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsPackageCacheWhoseAnchorIsTheVolumeRoot()
    {
        var volumeRoot = Path.GetPathRoot(_tempDirectory)
            ?? throw new InvalidOperationException("Test temp directory has no volume root.");
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:PackageCacheDirectory"] = Path.Combine(
                volumeRoot,
                "openlineops-content")
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            "beneath a non-root dedicated namespace anchor",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsDiagnosticTextRedactsBrokerAndArtifactUploadCredentials()
    {
        var options = StationAgentHostOptions.Load(CreateConfiguration());

        var diagnosticText = options.ToString();

        Assert.DoesNotContain("station-secret", diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain(ArtifactUploadToken, diagnosticText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", diagnosticText, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidArtifactUploadCredentialIsNotEchoedByStartupFailure()
    {
        var invalidSecret = ArtifactUploadToken + "=";
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:ArtifactUploadBearerToken"] = invalidSecret
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.DoesNotContain(invalidSecret, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("amqps://localhost:5671")]
    [InlineData("amqps://agent-1@localhost:5671")]
    [InlineData("amqps://agent-1:@localhost:5671")]
    [InlineData("amqps://guest:secret@localhost:5671")]
    [InlineData("amqps://GUEST:secret@localhost:5671")]
    public void LoadRejectsTlsBrokerWithoutDedicatedCredentials(string brokerUri)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:BrokerUri"] = brokerUri
        });

        var exception = Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));

        Assert.Contains("non-guest username", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAllowsCredentiallessBrokerOnlyWhenTlsIsExplicitlyDisabled()
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:BrokerUri"] = "amqp://localhost:5672",
            ["OpenLineOps:Agent:RequireBrokerTls"] = "false"
        });

        var options = StationAgentHostOptions.Load(configuration);

        Assert.False(options.RequireBrokerTls);
        Assert.Empty(options.BrokerUri.UserInfo);
    }

    [Theory]
    [InlineData("OpenLineOps:Agent:AgentId", "agent 1")]
    [InlineData("OpenLineOps:Agent:AgentId", "代理一")]
    [InlineData("OpenLineOps:Agent:StationId", "station 1")]
    [InlineData("OpenLineOps:Agent:StationId", "工站一")]
    [InlineData("OpenLineOps:Agent:StationSystemId", null)]
    [InlineData("OpenLineOps:Agent:StationSystemId", " station-system-1")]
    [InlineData("OpenLineOps:Agent:HeartbeatInterval", null)]
    [InlineData("OpenLineOps:Agent:HeartbeatInterval", "00:00:00.100")]
    [InlineData("OpenLineOps:Agent:HeartbeatInterval", "00:00:11")]
    public void LoadRejectsMissingOrInvalidPresenceIdentityAndHeartbeat(
        string settingName,
        string? value)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            [settingName] = value
        });

        Assert.Throws<InvalidDataException>(() =>
            StationAgentHostOptions.Load(configuration));
    }

    [Theory]
    [InlineData(
        "OpenLineOps:Agent:RuntimeExecutablePath",
        "..\\OpenLineOps.StationRuntime.exe",
        "OpenLineOps.StationRuntime.exe")]
    [InlineData(
        "OpenLineOps:Agent:PluginHostExecutablePath",
        "OpenLineOps.StationRuntime.exe",
        "OpenLineOps.PluginHost.exe")]
    [InlineData(
        "OpenLineOps:Agent:PythonScript:WorkerExecutablePath",
        "C:\\tools\\OpenLineOps.ScriptWorker.exe",
        "OpenLineOps.ScriptWorker.exe")]
    [InlineData(
        "OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeLauncherExecutable",
        "C:\\tools\\OpenLineOps.LeastPrivilegeLauncher.exe",
        "OpenLineOps.LeastPrivilegeLauncher.exe")]
    public void LoadRejectsExecutableConfigurationRedirection(
        string settingName,
        string configuredValue,
        string requiredFileName)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            [settingName] = configuredValue
        });

        var exception = Assert.Throws<InvalidDataException>(
            () => StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            $"must be exactly '{requiredFileName}'",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoadRejectsSafetyExecutableHardLinkedToStationRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimePath = Path.Combine(
            AppContext.BaseDirectory,
            StationAgentHostOptions.RuntimeExecutableFileName);
        // A Windows hard link must live on the same volume as its target. GitHub's
        // hosted runner places the test output on D: while Path.GetTempPath() is on
        // C:, so create this fixture beside the bundled runtime instead of in the
        // general test temp directory.
        var hardLinkPath = Path.Combine(
            AppContext.BaseDirectory,
            $"station-safety-hardlink-{Guid.NewGuid():N}.exe");
        if (!CreateHardLink(hardLinkPath, runtimePath, IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not create the safety-boundary hard-link fixture.");
        }

        try
        {
            var configuration = CreateConfiguration(new Dictionary<string, string?>
            {
                ["OpenLineOps:Agent:SafetyExecutablePath"] = hardLinkPath
            });

            var exception = Assert.Throws<InvalidDataException>(
                () => StationAgentHostOptions.Load(configuration));

            Assert.Contains(
                "independently reviewed safety actuator",
                exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(hardLinkPath);
        }
    }

    [Fact]
    public void LoadRejectsMissingSafetyExecutable()
    {
        var missingPath = Path.Combine(_tempDirectory, "missing-safety.exe");
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["OpenLineOps:Agent:SafetyExecutablePath"] = missingPath
        });

        var exception = Assert.Throws<FileNotFoundException>(
            () => StationAgentHostOptions.Load(configuration));

        Assert.Equal(missingPath, exception.FileName);
    }

    [Theory]
    [InlineData("OpenLineOps:Agent:PythonScript:Sandbox:RequireLeastPrivilegeExecution", "false")]
    [InlineData("OpenLineOps:Agent:PythonScript:Sandbox:IsolationMode", "ExternalProcess")]
    [InlineData("OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeIdentity", "another-user")]
    [InlineData("OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeNoInteractivePrompt", "false")]
    [InlineData("OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeArgumentsTemplate", "custom")]
    public void LoadRejectsPythonSandboxPolicyOverride(string settingName, string value)
    {
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            [settingName] = value
        });

        var exception = Assert.Throws<InvalidDataException>(
            () => StationAgentHostOptions.Load(configuration));

        Assert.Contains(
            "fixed non-interactive PerExecutionAppContainer policy",
            exception.Message,
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }

    private IConfiguration CreateConfiguration(
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenLineOps:Agent:AgentId"] = "agent-1",
            ["OpenLineOps:Agent:StationId"] = "station-1",
            ["OpenLineOps:Agent:StationSystemId"] = "station-system-1",
            ["OpenLineOps:Agent:HeartbeatInterval"] = "00:00:05",
            ["OpenLineOps:Agent:DataDirectory"] = Path.Combine(_tempDirectory, "data"),
            ["OpenLineOps:Agent:BrokerUri"] =
                "amqps://station-agent-1:station-secret@localhost:5671",
            ["OpenLineOps:Agent:PackageDistributionDirectory"] = Path.Combine(
                _tempDirectory,
                "packages"),
            ["OpenLineOps:Agent:PackageCacheDirectory"] = Path.Combine(
                _tempDirectory,
                "content-anchor",
                "content"),
            ["OpenLineOps:Agent:MaterialArrivalPackageContentSha256"] = new string('a', 64),
            ["OpenLineOps:Agent:TrustedPackagePublicKeyFiles:release"] = _trustedKeyPath,
            ["OpenLineOps:Agent:RuntimeExecutablePath"] =
                StationAgentHostOptions.RuntimeExecutableFileName,
            ["OpenLineOps:Agent:PluginHostExecutablePath"] =
                StationAgentHostOptions.PluginHostExecutableFileName,
            ["OpenLineOps:Agent:PythonScript:WorkerExecutablePath"] =
                StationAgentHostOptions.PythonScriptWorkerExecutableFileName,
            ["OpenLineOps:Agent:PythonScript:HostPythonRuntimeDllPath"] = _pythonRuntimePath,
            ["OpenLineOps:Agent:PythonScript:Sandbox:RequireLeastPrivilegeExecution"] = "true",
            ["OpenLineOps:Agent:PythonScript:Sandbox:IsolationMode"] = "LeastPrivilegeIdentity",
            ["OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeIdentity"] =
                StationAgentHostOptions.LeastPrivilegeIdentity,
            ["OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeLauncherExecutable"] =
                StationAgentHostOptions.LeastPrivilegeLauncherExecutableFileName,
            ["OpenLineOps:Agent:PythonScript:Sandbox:LeastPrivilegeNoInteractivePrompt"] = "true",
            ["OpenLineOps:Agent:CoordinatorBaseUri"] = "https://coordinator.test:7443/",
            ["OpenLineOps:Agent:ArtifactUploadBearerToken"] = ArtifactUploadToken,
            ["OpenLineOps:Agent:ArtifactUploadTimeout"] = "00:05:00",
            ["OpenLineOps:Agent:ExternalProgramAppContainerProfileNamespace"] =
                "openlineops-test",
            ["OpenLineOps:Agent:SafetyExecutablePath"] = _safetyExecutablePath
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                values[key] = value;
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
