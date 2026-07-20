using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenLineOps.ContentProtection;

public static class WindowsIdentityBoundNamedPipe
{
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServer(
        string pipeName,
        string authorizedPrincipalSid,
        int maximumServerInstances,
        int inputBufferSize,
        int outputBufferSize)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Identity-bound named pipes require Windows access-control lists.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumServerInstances, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputBufferSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputBufferSize, 0);
        var principal = ParseSid(authorizedPrincipalSid);
        var security = CreateSecurity(principal);
        var pipe = NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maximumServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inputBufferSize,
            outputBufferSize,
            security,
            HandleInheritability.None);
        try
        {
            Verify(pipe, principal);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Verify(PipeStream pipe, string authorizedPrincipalSid)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        Verify(pipe, ParseSid(authorizedPrincipalSid));
    }

    [SupportedOSPlatform("windows")]
    internal static PipeSecurity CreateSecurity(string authorizedPrincipalSid) =>
        CreateSecurity(ParseSid(authorizedPrincipalSid));

    [SupportedOSPlatform("windows")]
    private static PipeSecurity CreateSecurity(SecurityIdentifier principal)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(principal);
        security.AddAccessRule(new PipeAccessRule(
            principal,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void Verify(PipeStream pipe, SecurityIdentifier expectedPrincipal)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Identity-bound named pipes require Windows access-control lists.");
        }

        var security = pipe.GetAccessControl();
        if (!security.AreAccessRulesProtected)
        {
            throw new UnauthorizedAccessException(
                "Named pipe access rules must not inherit from a parent boundary.");
        }

        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !owner.Equals(expectedPrincipal))
        {
            throw new UnauthorizedAccessException(
                "Named pipe owner is not the configured Station service identity.");
        }

        var rules = security
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                targetType: typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        if (rules.Length != 1
            || rules[0].IsInherited
            || rules[0].AccessControlType != AccessControlType.Allow
            || rules[0].IdentityReference is not SecurityIdentifier rulePrincipal
            || !rulePrincipal.Equals(expectedPrincipal)
            || rules[0].PipeAccessRights != PipeAccessRights.FullControl)
        {
            throw new UnauthorizedAccessException(
                "Named pipe ACL must grant only the configured Station service identity full control.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier ParseSid(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var sid = new SecurityIdentifier(value);
        if (!string.Equals(sid.Value, value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Named pipe authorized principal SID must be canonical.");
        }

        return sid;
    }
}
