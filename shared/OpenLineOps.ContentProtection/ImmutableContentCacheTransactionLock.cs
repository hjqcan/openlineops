using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace OpenLineOps.ContentProtection;

public sealed class ImmutableContentCacheTransactionLock : IDisposable
{
    private readonly string _name;
    private readonly string? _stationServiceSid;
    private readonly Mutex _guard;
    private int _disposed;

    public ImmutableContentCacheTransactionLock(
        string cacheRootDirectory,
        string? stationServiceSid)
    {
        var cacheIdentity = ImmutableContentProtector.GetStableDirectoryIdentity(
            cacheRootDirectory);
        _name = CreateName(cacheIdentity);
        _stationServiceSid = stationServiceSid;
        _guard = OpenVerifiedHandle(_name, stationServiceSid);
    }

    public async ValueTask<ImmutableContentCacheTransactionLease> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return await ImmutableContentCacheTransactionLease.AcquireAsync(
                _name,
                _stationServiceSid,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _guard.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string CreateName(string cacheIdentity)
    {
        var mutexHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(cacheIdentity)));
        return (OperatingSystem.IsWindows() ? "Global\\" : string.Empty)
               + "OpenLineOps.PackageCache."
               + mutexHash;
    }

    internal static Mutex OpenVerifiedHandle(
        string name,
        string? stationServiceSid) => OperatingSystem.IsWindows()
        ? OpenVerifiedWindowsHandle(name, stationServiceSid)
        : new Mutex(initiallyOwned: false, name);

    [SupportedOSPlatform("windows")]
    private static Mutex OpenVerifiedWindowsHandle(
        string name,
        string? stationServiceSid)
    {
        const MutexRights participantRights = MutexRights.Modify
                                              | MutexRights.Synchronize
                                              | MutexRights.ReadPermissions;
        SecurityIdentifier stationService = new(
            WindowsStationServiceIdentityReader.RequireCanonicalServiceSid(
                stationServiceSid,
                nameof(stationServiceSid)));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        var ownerRights = new SecurityIdentifier(
            WellKnownSidType.WinCreatorOwnerRightsSid,
            null);
        var localService = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier currentUser = identity.User
                                         ?? throw new InvalidOperationException(
                                             "Immutable content transaction token has no user SID.");
        SecurityIdentifier expectedOwner;
        if (currentUser.Equals(localService))
        {
            expectedOwner = stationService;
        }
        else if (currentUser.Equals(system))
        {
            expectedOwner = system;
        }
        else if (IsEnabledAdministrator(identity, administrators))
        {
            expectedOwner = administrators;
        }
        else
        {
            expectedOwner = currentUser;
        }

        var principalRights = new Dictionary<SecurityIdentifier, MutexRights>
        {
            [system] = MutexRights.FullControl,
            [administrators] = MutexRights.FullControl,
            [stationService] = participantRights
        };
        if (expectedOwner.Equals(currentUser))
        {
            principalRights[currentUser] = participantRights;
        }

        var security = new MutexSecurity();
        security.SetOwner(expectedOwner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach ((SecurityIdentifier principal, MutexRights rights) in principalRights)
        {
            security.AddAccessRule(new MutexAccessRule(
                principal,
                rights,
                AccessControlType.Allow));
        }

        security.AddAccessRule(new MutexAccessRule(
            ownerRights,
            MutexRights.ChangePermissions | MutexRights.TakeOwnership,
            AccessControlType.Deny));

        Mutex mutex;
        try
        {
            mutex = MutexAcl.OpenExisting(name, participantRights);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            try
            {
                mutex = MutexAcl.Create(
                    initiallyOwned: false,
                    name,
                    out var createdNew,
                    security);
                if (!createdNew)
                {
                    mutex.Dispose();
                    mutex = MutexAcl.OpenExisting(name, participantRights);
                }
            }
            catch (UnauthorizedAccessException)
            {
                mutex = MutexAcl.OpenExisting(name, participantRights);
            }
        }

        VerifyWindowsSecurity(
            mutex.GetAccessControl(),
            expectedOwner,
            principalRights,
            ownerRights);
        return mutex;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledAdministrator(
        WindowsIdentity identity,
        SecurityIdentifier administrators)
    {
        try
        {
            return new WindowsPrincipal(identity).IsInRole(administrators);
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyWindowsSecurity(
        MutexSecurity security,
        SecurityIdentifier expectedOwner,
        Dictionary<SecurityIdentifier, MutexRights> principalRights,
        SecurityIdentifier ownerRights)
    {
        SecurityIdentifier actualOwner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                                         ?? throw new InvalidDataException(
                                             "Immutable content transaction mutex owner SID is unavailable.");
        MutexAccessRule[] rules = [.. security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<MutexAccessRule>()];
        if (!security.AreAccessRulesProtected
            || !expectedOwner.Equals(actualOwner)
            || rules.Length != principalRights.Count + 1
            || rules.Any(rule => rule.IsInherited)
            || principalRights.Any(pair => !rules.Any(rule =>
                pair.Key.Equals(rule.IdentityReference)
                && rule.AccessControlType == AccessControlType.Allow
                && rule.MutexRights == pair.Value))
            || !rules.Any(rule =>
                ownerRights.Equals(rule.IdentityReference)
                && rule.AccessControlType == AccessControlType.Deny
                && rule.MutexRights
                == (MutexRights.ChangePermissions | MutexRights.TakeOwnership)))
        {
            throw new InvalidDataException(
                "Immutable content transaction mutex owner and ACL are not exact.");
        }
    }
}

public sealed class ImmutableContentCacheTransactionLease : IAsyncDisposable
{
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _ownerTask;
    private int _disposed;

    private ImmutableContentCacheTransactionLease(
        string name,
        string? stationServiceSid,
        CancellationToken cancellationToken)
    {
        var acquired = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ownerTask = Task.Factory.StartNew(
            () => OwnMutex(
                name,
                stationServiceSid,
                acquired,
                _release.Task,
                cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Acquired = acquired.Task;
    }

    private Task Acquired { get; }

    internal static async ValueTask<ImmutableContentCacheTransactionLease> AcquireAsync(
        string name,
        string? stationServiceSid,
        CancellationToken cancellationToken)
    {
        var lease = new ImmutableContentCacheTransactionLease(
            name,
            stationServiceSid,
            cancellationToken);
        try
        {
            await lease.Acquired.ConfigureAwait(false);
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.TrySetResult();
        await _ownerTask.ConfigureAwait(false);
    }

    private static void OwnMutex(
        string name,
        string? stationServiceSid,
        TaskCompletionSource acquired,
        Task release,
        CancellationToken cancellationToken)
    {
        using Mutex mutex = ImmutableContentCacheTransactionLock.OpenVerifiedHandle(
            name,
            stationServiceSid);
        var ownsMutex = false;
        try
        {
            while (!ownsMutex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ownsMutex = mutex.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            acquired.TrySetResult();
            release.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException exception)
        {
            acquired.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            acquired.TrySetException(exception);
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
