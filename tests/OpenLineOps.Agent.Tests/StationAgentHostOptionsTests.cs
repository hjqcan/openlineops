using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace OpenLineOps.Agent.Tests;

public sealed class StationAgentHostOptionsTests : IDisposable
{
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
            "fixed non-interactive RestrictedCurrentLowIntegrity policy",
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
            ["OpenLineOps:Agent:DataDirectory"] = Path.Combine(_tempDirectory, "data"),
            ["OpenLineOps:Agent:BrokerUri"] = "amqps://localhost:5671",
            ["OpenLineOps:Agent:PackageDistributionDirectory"] = Path.Combine(
                _tempDirectory,
                "packages"),
            ["OpenLineOps:Agent:MaterialArrivalPackageContentSha256"] = new string('a', 64),
            ["OpenLineOps:Agent:MaterialArrivalPipeName"] = "openlineops-test-arrival",
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
            ["OpenLineOps:Agent:ArtifactExchangeDirectory"] = Path.Combine(
                _tempDirectory,
                "artifact-exchange"),
            ["OpenLineOps:Agent:AllowedRestrictedExternalProgramHostAccounts:0"] =
                "OPENLINEOPS\\vendor-host",
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
