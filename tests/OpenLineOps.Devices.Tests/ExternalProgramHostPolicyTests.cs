using System.Runtime.Versioning;
using System.Security.Principal;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

namespace OpenLineOps.Devices.Tests;

public sealed class ExternalProgramHostPolicyTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsIdentityReaderRequestsQueryAndDuplicateAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            TokenAccessLevels.Query | TokenAccessLevels.Duplicate,
            WindowsExternalProgramHostIdentityReader.RequiredTokenAccess);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsIdentityReaderRecognizesAuthenticatedUacFilteredTokenFacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identity = WindowsExternalProgramHostIdentityReader.CreateIdentity(
            "FACTORY\\OpenLineOpsAgent",
            new SecurityIdentifier("S-1-5-21-1000-1000-1000-1001"),
            "Negotiate",
            administratorProbe: static () => false);

        Assert.True(identity.IsAuthenticated);
        Assert.False(identity.IsSystem);
        Assert.False(identity.IsAdministrator);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsIdentityReaderPreservesAdministrativeTokenFacts()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identity = WindowsExternalProgramHostIdentityReader.CreateIdentity(
            "FACTORY\\Administrator",
            new SecurityIdentifier("S-1-5-21-1000-1000-1000-500"),
            "Negotiate",
            administratorProbe: static () => true);

        Assert.True(identity.IsAdministrator);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsIdentityReaderFailsClosedWhenAdministratorProbeIsDenied()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var identity = WindowsExternalProgramHostIdentityReader.CreateIdentity(
            "FACTORY\\OpenLineOpsAgent",
            new SecurityIdentifier("S-1-5-21-1000-1000-1000-1001"),
            "Negotiate",
            administratorProbe: static () =>
                throw new System.Security.SecurityException("Access denied."));

        Assert.True(identity.IsAdministrator);
    }

    [Fact]
    public void EffectivePolicyUsesTheStrictIntersectionOfHostAndFrozenResourceLimits()
    {
        var host = CreateOptions(requireIdentity: false);
        host.MaximumStandardOutputBytes = 100;
        host.MaximumStandardErrorBytes = 200;
        host.MaximumArtifactCount = 9;
        host.MaximumArtifactBytes = 1_000;
        host.MaximumTotalArtifactBytes = 3_000;
        host.MaximumProcessCount = 4;
        host.MaximumWorkingSetBytes = 800;
        host.MaximumJobMemoryBytes = 2_000;
        host.MaximumCpuTimeMilliseconds = 500;
        host.MaximumExecutionTimeMilliseconds = 700;
        host.AllowedInheritedEnvironmentVariables.Clear();
        host.AllowedInheritedEnvironmentVariables.Add("SystemRoot");
        host.AllowedInheritedEnvironmentVariables.Add("HOST_ONLY_SECRET");
        var request = CreateRequest(
            networkAccessAllowed: true,
            allowedEnvironmentVariables: ["SystemRoot", "RESOURCE_ONLY_SECRET"],
            maximumProcessCount: 8,
            maximumWorkingSetBytes: 900,
            maximumCpuTimeMilliseconds: 600,
            maximumStandardOutputBytes: 150,
            maximumStandardErrorBytes: 150,
            maximumArtifactCount: 7,
            maximumArtifactBytes: 900,
            maximumTotalArtifactBytes: 4_000,
            timeout: TimeSpan.FromMilliseconds(800));

        var policy = EffectiveExternalProgramPolicy.Create(host, request);

        Assert.Equal(TimeSpan.FromMilliseconds(700), policy.ExecutionTimeout);
        Assert.Equal(100, policy.MaximumStandardOutputBytes);
        Assert.Equal(150, policy.MaximumStandardErrorBytes);
        Assert.Equal(7, policy.MaximumArtifactCount);
        Assert.Equal(900, policy.MaximumArtifactBytes);
        Assert.Equal(3_000, policy.MaximumTotalArtifactBytes);
        Assert.Equal(4, policy.ProcessLimits.ActiveProcessLimit);
        Assert.Equal(800, policy.ProcessLimits.ProcessMemoryLimitBytes);
        Assert.Equal(2_000, policy.ProcessLimits.JobMemoryLimitBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.ProcessLimits.CpuTimeLimit);
        Assert.Equal(["SystemRoot"], policy.InheritedEnvironmentVariables);
    }

    [Fact]
    public void RestrictedIdentityMismatchFailsClosedBeforeLaunch()
    {
        var options = CreateOptions(requireIdentity: true);
        options.AllowedRestrictedHostAccounts.Add("FACTORY\\OpenLineOpsAgent");
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                "FACTORY\\DifferentAccount",
                "S-1-5-21-1000",
                IsAuthenticated: true,
                IsSystem: false,
                IsAdministrator: false)));

        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: true).Policy));
    }

    [Fact]
    public void RestrictedIdentityRejectsAdministrativeTokensEvenWhenAllowlisted()
    {
        var options = CreateOptions(requireIdentity: true);
        options.AllowedRestrictedHostSids.Add("S-1-5-21-1000");
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                "FACTORY\\OpenLineOpsAgent",
                "S-1-5-21-1000",
                IsAuthenticated: true,
                IsSystem: false,
                IsAdministrator: true)));

        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: true).Policy));
    }

    [Fact]
    public void NetworkDenialRequiresAppContainerIsolation()
    {
        var options = CreateOptions(requireIdentity: true);
        options.AllowedRestrictedHostSids.Add("S-1-5-21-1000");
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                "FACTORY\\OpenLineOpsAgent",
                "S-1-5-21-1000",
                IsAuthenticated: true,
                IsSystem: false,
                IsAdministrator: false)));

        _ = enforcer.Enforce(CreateRequest(networkAccessAllowed: false).Policy);

        options.RequireAppContainerIsolation = false;
        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: false).Policy));
    }

    [Fact]
    public void LocalStudioOptOutRejectsAResourceThatDeclaresNetworkDenial()
    {
        var options = CreateOptions(requireIdentity: false);
        options.RequireAppContainerIsolation = false;
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                "developer",
                "developer",
                IsAuthenticated: true,
                IsSystem: false,
                IsAdministrator: false)));

        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: false).Policy));
    }

    [Fact]
    public void WindowsWorkspaceRootRejectsAProcessCurrentDirectoryAtTheNativeLimit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = CreateOptions(requireIdentity: false);
        options.WorkspaceRootPath = Path.Combine(
            Path.GetTempPath(),
            "OpenLineOps external program workspace " + Guid.NewGuid().ToString("N"),
            new string('a', 96),
            new string('b', 96));

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("too long", exception.Message, StringComparison.Ordinal);
    }

    private static ExternalProgramHostOptions CreateOptions(bool requireIdentity)
    {
        var root = Path.GetTempPath();
        return new ExternalProgramHostOptions
        {
            WorkspaceRootPath = root,
            EvidenceRootPath = root,
            RequireRestrictedHostIdentity = requireIdentity,
            RequireImmutableContentProtection = false,
            RequireAppContainerIsolation = true,
            AppContainerProfileName = "OpenLineOps.Tests.ExternalPrograms"
        };
    }

    private static ExternalProgramExecutionRequest CreateRequest(
        bool networkAccessAllowed,
        IReadOnlyCollection<string>? allowedEnvironmentVariables = null,
        int maximumProcessCount = 4,
        long maximumWorkingSetBytes = 1_024,
        long maximumCpuTimeMilliseconds = 1_000,
        int maximumStandardOutputBytes = 1_024,
        int maximumStandardErrorBytes = 1_024,
        int maximumArtifactCount = 8,
        long maximumArtifactBytes = 2_048,
        long maximumTotalArtifactBytes = 4_096,
        TimeSpan? timeout = null) => new(
        "resource-main",
        Guid.NewGuid(),
        Guid.NewGuid(),
        Path.GetTempPath(),
        "external-programs/resource-main",
        "external-programs/resource-main/files/helper.exe",
        1,
        new string('a', 64),
        [new ExternalProgramExecutionFile("files/helper.exe", 1, new string('a', 64))],
        [],
        "{}",
        timeout ?? TimeSpan.FromSeconds(2),
        new ExternalProgramExecutionPolicy(
            "Restricted",
            networkAccessAllowed,
            allowedEnvironmentVariables ?? [],
            maximumProcessCount,
            maximumWorkingSetBytes,
            maximumCpuTimeMilliseconds,
            maximumStandardOutputBytes,
            maximumStandardErrorBytes,
            maximumArtifactCount,
            maximumArtifactBytes,
            maximumTotalArtifactBytes));

    private sealed class IdentityReader(ExternalProgramHostIdentity identity)
        : IExternalProgramHostIdentityReader
    {
        public ExternalProgramHostIdentity Read() => identity;
    }

}
