using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace OpenLineOps.WindowsSecurity;

public sealed record WindowsStationServiceIdentity(
    string HostAccountSid,
    string ServiceSid,
    bool ServiceLogonSidEnabled,
    bool IsRestrictedToken,
    bool ServiceSidEnabled,
    bool ServiceSidOwnerEligible,
    bool ServiceSidRestricted);

public static class WindowsStationServiceIdentityReader
{
    public const string LocalServiceSid = "S-1-5-19";
    public const string ServiceLogonSid = "S-1-5-6";

    private const string ServiceSidPrefix = "S-1-5-80-";
    private const uint SeGroupEnabled = 0x00000004;
    private const uint SeGroupOwner = 0x00000008;
    private const uint SeGroupUseForDenyOnly = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;

    public static string RequireCanonicalServiceName(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (!IsCanonicalServiceName(value))
        {
            throw new InvalidDataException(
                $"{parameterName} must contain 1-80 ASCII letters, digits, periods, underscores, or hyphens.");
        }

        return value!;
    }

    public static bool IsCanonicalServiceName(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 80
        && value.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.'
            or '_'
            or '-');

    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "Windows defines canonical per-service SIDs with SHA-1 over the uppercase UTF-16 service name.")]
    public static string ServiceSidFromNameRequired(string serviceName)
    {
        var canonicalServiceName = RequireCanonicalServiceName(
            serviceName,
            nameof(serviceName));
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(
            canonicalServiceName.ToUpperInvariant()));
        var subAuthorities = new string[5];
        for (var index = 0; index < subAuthorities.Length; index++)
        {
            subAuthorities[index] = BinaryPrimitives
                .ReadUInt32LittleEndian(hash.AsSpan(index * sizeof(uint), sizeof(uint)))
                .ToString(CultureInfo.InvariantCulture);
        }

        return ServiceSidPrefix + string.Join('-', subAuthorities);
    }

    public static string RequireCanonicalServiceSid(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (!IsCanonicalServiceSid(value))
        {
            throw new InvalidDataException(
                $"{parameterName} must be an exact canonical Windows service SID with five decimal subauthorities.");
        }

        return value!;
    }

    public static bool IsCanonicalServiceSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(ServiceSidPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split('-');
        if (parts.Length != 9
            || parts[0] != "S"
            || parts[1] != "1"
            || parts[2] != "5"
            || parts[3] != "80")
        {
            return false;
        }

        for (var index = 4; index < parts.Length; index++)
        {
            if (!uint.TryParse(
                    parts[index],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var subAuthority)
                || !string.Equals(
                    subAuthority.ToString(CultureInfo.InvariantCulture),
                    parts[index],
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    [SupportedOSPlatform("windows")]
    public static WindowsStationServiceIdentity ReadRequired(string requiredServiceSid)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Station restricted service identity validation requires Windows.");
        }

        var canonicalServiceSid = RequireCanonicalServiceSid(
            requiredServiceSid,
            nameof(requiredServiceSid));
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User?.Value
                      ?? throw new InvalidOperationException(
                          "Current Windows token has no user SID.");
        List<TokenSid> groups = ReadTokenSids(identity.AccessToken, TokenInformationClass.TokenGroups);
        List<TokenSid> restrictedSids = ReadTokenSids(
            identity.AccessToken,
            TokenInformationClass.TokenRestrictedSids);
        var serviceLogonSidEnabled = groups.Any(group =>
            IsEnabledGroup(group, ServiceLogonSid));
        var enabled = groups.Any(group => IsEnabledGroup(group, canonicalServiceSid));
        var ownerEligible = groups.Any(group =>
            string.Equals(group.Sid, canonicalServiceSid, StringComparison.Ordinal)
            && (group.Attributes & SeGroupOwner) != 0
            && (group.Attributes & SeGroupUseForDenyOnly) == 0);
        var restricted = restrictedSids.Any(group =>
            string.Equals(group.Sid, canonicalServiceSid, StringComparison.Ordinal));
        var isRestrictedToken = ReadIsRestrictedToken(identity.AccessToken);
        var result = new WindowsStationServiceIdentity(
            userSid,
            canonicalServiceSid,
            serviceLogonSidEnabled,
            isRestrictedToken,
            enabled,
            ownerEligible,
            restricted);
        Validate(result);
        return result;
    }

    public static void Validate(WindowsStationServiceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var canonicalServiceSid = RequireCanonicalServiceSid(
            identity.ServiceSid,
            nameof(identity.ServiceSid));
        if (!string.Equals(
                identity.HostAccountSid,
                LocalServiceSid,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Station processes must run with NT AUTHORITY\\LocalService as the token user.");
        }

        if (!identity.ServiceLogonSidEnabled)
        {
            throw new InvalidOperationException(
                $"Station process token does not contain enabled Windows service-logon SID '{ServiceLogonSid}'.");
        }

        if (!identity.IsRestrictedToken)
        {
            throw new InvalidOperationException(
                "Station process token is not a restricted Windows token.");
        }

        if (!identity.ServiceSidEnabled)
        {
            throw new InvalidOperationException(
                $"Station service SID '{canonicalServiceSid}' is not enabled in TokenGroups.");
        }

        if (!identity.ServiceSidOwnerEligible)
        {
            throw new InvalidOperationException(
                "Station processes require the exact service SID to be eligible as an object owner.");
        }

        if (!identity.ServiceSidRestricted)
        {
            throw new InvalidOperationException(
                $"Station service SID '{canonicalServiceSid}' is absent from TokenRestrictedSids.");
        }
    }

    private static bool IsEnabledGroup(TokenSid group, string requiredSid) =>
        string.Equals(group.Sid, requiredSid, StringComparison.Ordinal)
        && (group.Attributes & SeGroupEnabled) != 0
        && (group.Attributes & SeGroupUseForDenyOnly) == 0;

    [SupportedOSPlatform("windows")]
    internal static bool ReadIsRestrictedToken(SafeAccessTokenHandle token) =>
        IsTokenRestricted(token);

    [SupportedOSPlatform("windows")]
    private static List<TokenSid> ReadTokenSids(
        SafeAccessTokenHandle token,
        TokenInformationClass informationClass)
    {
        _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var requiredBytes);
        var error = Marshal.GetLastPInvokeError();
        if (requiredBytes == 0 || error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, $"Could not size Windows {informationClass} data.");
        }

        var buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
        try
        {
            if (!GetTokenInformation(
                    token,
                    informationClass,
                    buffer,
                    requiredBytes,
                    out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Could not read Windows {informationClass} data.");
            }

            var count = Marshal.ReadInt32(buffer);
            var itemOffset = Marshal.OffsetOf<TokenGroups>(nameof(TokenGroups.Groups)).ToInt32();
            var itemSize = Marshal.SizeOf<SidAndAttributes>();
            var result = new List<TokenSid>(count);
            for (var index = 0; index < count; index++)
            {
                var itemAddress = IntPtr.Add(buffer, checked(itemOffset + index * itemSize));
                SidAndAttributes item = Marshal.PtrToStructure<SidAndAttributes>(itemAddress);
                result.Add(new TokenSid(
                    new SecurityIdentifier(item.Sid).Value,
                    item.Attributes));
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle tokenHandle);

    private enum TokenInformationClass
    {
        TokenGroups = 2,
        TokenRestrictedSids = 11
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroups
    {
        public uint GroupCount;
        public SidAndAttributes Groups;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    private sealed record TokenSid(string Sid, uint Attributes);
}
