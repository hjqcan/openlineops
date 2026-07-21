using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.Agent.Tests;

[SupportedOSPlatform("windows")]
internal sealed class WindowsTokenAccessLease : IDisposable
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int TokenTypeInformationClass = 8;
    private const int TokenPrimary = 1;
    private const string ObjectDescription = "source Station primary token";

    private readonly WindowsKernelObjectAccessLease _lease;

    private WindowsTokenAccessLease(WindowsKernelObjectAccessLease lease)
    {
        _lease = lease;
    }

    public static WindowsTokenAccessLease Acquire(
        SafeProcessHandle retainedProcessHandle,
        uint processId,
        long expectedCreatedAtUtcTicks,
        SecurityIdentifier bridgeServiceSid)
    {
        ArgumentNullException.ThrowIfNull(bridgeServiceSid);
        WindowsProcessAccessLease.ValidateExactProcessHandle(
            retainedProcessHandle,
            processId,
            expectedCreatedAtUtcTicks,
            "token-DACL source Station");

        var token = OpenTokenRequired(
            retainedProcessHandle,
            ReadControl | WriteDac | TokenQuery,
            "scoped token-DACL lease");
        try
        {
            ValidatePrimaryToken(token);
        }
        catch
        {
            token.Dispose();
            throw;
        }

        return new WindowsTokenAccessLease(
            WindowsKernelObjectAccessLease.AcquireOwnedHandle(
                token,
                bridgeServiceSid,
                unchecked((int)(TokenQuery | TokenDuplicate)),
                ObjectDescription));
    }

    internal static string ReadDaclSddl(SafeProcessHandle processHandle)
    {
        using var token = OpenTokenRequired(
            processHandle,
            ReadControl,
            "DACL inspection");
        return WindowsKernelObjectAccessLease.ReadDaclSddl(
            token,
            ObjectDescription);
    }

    internal static bool HasExactQueryAndDuplicateAce(
        SafeProcessHandle processHandle,
        SecurityIdentifier bridgeServiceSid)
    {
        using var token = OpenTokenRequired(
            processHandle,
            ReadControl,
            "DACL inspection");
        return WindowsKernelObjectAccessLease.HasExactAce(
            token,
            bridgeServiceSid,
            unchecked((int)(TokenQuery | TokenDuplicate)),
            ObjectDescription);
    }

    public void Dispose() => _lease.Dispose();

    private static SafeAccessTokenHandle OpenTokenRequired(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        string purpose)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        if (!OpenProcessToken(
                processHandle,
                desiredAccess,
                out var token))
        {
            var error = Marshal.GetLastPInvokeError();
            token.Dispose();
            throw new Win32Exception(
                error,
                $"Could not open the source Station primary token for {purpose}; Win32 error {error}.");
        }

        return token;
    }

    private static void ValidatePrimaryToken(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(
                token,
                TokenTypeInformationClass,
                out var tokenType,
                sizeof(int),
                out var returnedLength))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(
                error,
                $"Could not validate the source Station primary token type; Win32 error {error}.");
        }
        if (returnedLength != sizeof(int) || tokenType != TokenPrimary)
        {
            throw new InvalidDataException(
                "The source Station process did not expose a primary token for its scoped DACL lease.");
        }
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "OpenProcessToken",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
