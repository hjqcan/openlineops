using System.Runtime.Versioning;
using System.Security.Principal;
using OpenLineOps.ContentProtection;
using OpenLineOps.Devices.Application.Execution.ExternalPrograms;
using OpenLineOps.Devices.Infrastructure.Execution.ExternalPrograms;

namespace OpenLineOps.Devices.Tests;

public sealed class ExternalProgramHostPolicyTests
{
    private const string ServiceSid = "S-1-5-80-123-456-789-1011-1213";
    private const string OtherServiceSid = "S-1-5-80-321-654-987-1101-1312";

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsIdentityReaderRequestsOnlyQueryAccess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            TokenAccessLevels.Query,
            WindowsExternalProgramHostIdentityReader.RequiredTokenAccess);
    }

    [Fact]
    public void RestrictedServiceIdentityAcceptsExactLocalServiceTokenFacts()
    {
        WindowsStationServiceIdentityReader.Validate(new WindowsStationServiceIdentity(
            WindowsStationServiceIdentityReader.LocalServiceSid,
            ServiceSid,
            ServiceLogonSidEnabled: true,
            IsRestrictedToken: true,
            ServiceSidEnabled: true,
            ServiceSidOwnerEligible: true,
            ServiceSidRestricted: true));
    }

    [Theory]
    [InlineData("S-1-5-18", true, true, true, true, true)]
    [InlineData("S-1-5-19", false, true, true, true, true)]
    [InlineData("S-1-5-19", true, false, true, true, true)]
    [InlineData("S-1-5-19", true, true, false, true, true)]
    [InlineData("S-1-5-19", true, true, true, false, true)]
    [InlineData("S-1-5-19", true, true, true, true, false)]
    public void RestrictedServiceIdentityRejectsWrongHostOrTokenMembership(
        string hostAccountSid,
        bool serviceLogonSidEnabled,
        bool isRestrictedToken,
        bool enabled,
        bool ownerEligible,
        bool restricted)
    {
        Assert.Throws<InvalidOperationException>(() =>
            WindowsStationServiceIdentityReader.Validate(new WindowsStationServiceIdentity(
                hostAccountSid,
                ServiceSid,
                serviceLogonSidEnabled,
                isRestrictedToken,
                enabled,
                ownerEligible,
                restricted)));
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
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                WindowsStationServiceIdentityReader.LocalServiceSid,
                OtherServiceSid,
                ServiceLogonSidEnabled: true,
                IsRestrictedToken: true,
                ServiceSidEnabled: true,
                ServiceSidRestricted: true)));

        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: true).Policy));
    }

    [Fact]
    public void RestrictedIdentityRejectsNonLocalServiceTokenUser()
    {
        var options = CreateOptions(requireIdentity: true);
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                "S-1-5-18",
                ServiceSid,
                ServiceLogonSidEnabled: true,
                IsRestrictedToken: true,
                ServiceSidEnabled: true,
                ServiceSidRestricted: true)));

        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: true).Policy));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RestrictedIdentityRequiresEnabledAndRestrictedServiceSid(
        bool enabled,
        bool restricted)
    {
        var options = CreateOptions(requireIdentity: true);
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                WindowsStationServiceIdentityReader.LocalServiceSid,
                ServiceSid,
                ServiceLogonSidEnabled: true,
                IsRestrictedToken: true,
                enabled,
                restricted)));

        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: true).Policy));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RestrictedIdentityRequiresServiceLogonAndRestrictedTokenFacts(
        bool serviceLogonSidEnabled,
        bool isRestrictedToken)
    {
        var options = CreateOptions(requireIdentity: true);
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                WindowsStationServiceIdentityReader.LocalServiceSid,
                ServiceSid,
                serviceLogonSidEnabled,
                isRestrictedToken,
                ServiceSidEnabled: true,
                ServiceSidRestricted: true)));

        Assert.Throws<InvalidOperationException>(() =>
            enforcer.Enforce(CreateRequest(networkAccessAllowed: true).Policy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-80-0123-456-789-1011-1213")]
    public void OptionsRejectMissingOrNoncanonicalRestrictedServiceSid(string? serviceSid)
    {
        var options = CreateOptions(requireIdentity: true);
        options.RestrictedServiceSid = serviceSid;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void OptionsRejectImmutableContentWithoutAppContainerIsolation()
    {
        var options = CreateOptions(requireIdentity: true);
        options.RequireImmutableContentProtection = true;
        options.RequireAppContainerIsolation = false;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("requires AppContainer isolation", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkDenialRequiresAppContainerIsolation()
    {
        var options = CreateOptions(requireIdentity: true);
        var enforcer = new ExternalProgramHostPolicyEnforcer(
            options,
            new IdentityReader(new ExternalProgramHostIdentity(
                WindowsStationServiceIdentityReader.LocalServiceSid,
                ServiceSid,
                ServiceLogonSidEnabled: true,
                IsRestrictedToken: true,
                ServiceSidEnabled: true,
                ServiceSidRestricted: true)));

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
                string.Empty,
                string.Empty,
                ServiceLogonSidEnabled: false,
                IsRestrictedToken: false,
                ServiceSidEnabled: false,
                ServiceSidRestricted: false)));

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
            AppContainerProfileName = "OpenLineOps.Tests.ExternalPrograms",
            RestrictedServiceSid = requireIdentity ? ServiceSid : null
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
        public ExternalProgramHostIdentity Read(string requiredServiceSid) => identity;
    }

}
