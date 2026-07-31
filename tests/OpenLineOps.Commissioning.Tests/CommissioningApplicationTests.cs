using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Commissioning.Application.Contracts;
using OpenLineOps.Commissioning.Application.Services;
using OpenLineOps.Commissioning.Domain.Sessions;
using OpenLineOps.Commissioning.Infrastructure.Persistence;
using OpenLineOps.Commissioning.Infrastructure.Security;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Commissioning.Tests;

public sealed class CommissioningApplicationTests
{
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 7, 31, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartEnforcesRoleCapabilityAndExclusiveStationLease()
    {
        var service = CreateService(out _, out _);

        var first = await service.StartAsync(StartCommand("session-a", "station-a"));
        var conflict = await service.StartAsync(StartCommand("session-b", "station-a"));
        var secondStation = await service.StartAsync(StartCommand("session-c", "station-b"));

        Assert.True(first.IsSuccess);
        Assert.True(conflict.IsFailure);
        Assert.Equal(
            "Conflict.Commissioning.StationLeaseConflict",
            conflict.Error.Code);
        Assert.True(secondStation.IsSuccess);
    }

    [Fact]
    public async Task StartRejectsCapabilityNotGrantedToRole()
    {
        var policy = new CommissioningRoleAccessPolicy(
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["engineer-a"] = new HashSet<string>(StringComparer.Ordinal)
                {
                    "DiagnosticsEngineer"
                }
            },
            new Dictionary<string, IReadOnlySet<CommissioningCapability>>
            {
                ["DiagnosticsEngineer"] =
                    new HashSet<CommissioningCapability>
                    {
                        CommissioningCapability.DeviceDiagnostics
                    }
            });
        var service = new CommissioningService(
            new InMemoryCommissioningSessionRepository(),
            policy,
            new MutableClock(StartedAtUtc));
        var command = StartCommand(
            "session-a",
            "station-a",
            [
                CommissioningCapability.DeviceDiagnostics,
                CommissioningCapability.ManualCommand
            ]) with
        {
            AuthorizedRole = "DiagnosticsEngineer"
        };

        var result = await service.StartAsync(command);

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Commissioning.AccessDenied", result.Error.Code);
    }

    [Fact]
    public async Task StartRejectsActorClaimingAnUngrantedRole()
    {
        var service = CreateService(out _, out _);
        var command = StartCommand("session-a", "station-a") with
        {
            ActorId = "engineer-other"
        };

        var result = await service.StartAsync(command);

        Assert.True(result.IsFailure);
        Assert.Equal("Conflict.Commissioning.AccessDenied", result.Error.Code);
    }

    [Fact]
    public async Task RestrictedManualCommandAppendsAuditAndAdvancesRevision()
    {
        var service = CreateService(out _, out var clock);
        await service.StartAsync(StartCommand("session-a", "station-a"));
        clock.UtcNow = StartedAtUtc.AddMinutes(1);

        var result = await service.AuthorizeManualCommandAsync(
            new AuthorizeCommissioningManualCommand(
                "session-a",
                "engineer-a",
                "command.clamp",
                StationMode.Maintenance,
                CommissioningActionSafetyClass.Motion,
                DebugActionWhitelisted: true));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.Revision);
        Assert.Equal(
            CommissioningAuditKind.ManualCommandAuthorized,
            result.Value.AuditTrail.Last().Kind);
    }

    [Fact]
    public async Task ExpiredLeaseIsAuditedBeforeAnotherSessionAcquiresStation()
    {
        var service = CreateService(out _, out var clock);
        await service.StartAsync(
            StartCommand("session-a", "station-a") with
            {
                Duration = TimeSpan.FromMinutes(5)
            });
        clock.UtcNow = StartedAtUtc.AddMinutes(5);

        var replacement = await service.StartAsync(
            StartCommand("session-b", "station-a"));
        var expired = await service.GetAsync("session-a");

        Assert.True(replacement.IsSuccess);
        Assert.True(expired.IsSuccess);
        Assert.Equal(CommissioningSessionStatus.Expired, expired.Value.Status);
        Assert.Equal(
            CommissioningAuditKind.SessionExpired,
            expired.Value.AuditTrail.Last().Kind);
    }

    [Fact]
    public async Task UnsafeRecoveryNeedsHumanDispositionAndNeverAutoReplays()
    {
        var service = CreateService(out _, out var clock);
        await service.StartAsync(StartCommand("session-a", "station-a"));
        clock.UtcNow = StartedAtUtc.AddMinutes(1);

        var interrupted = await service.RecoverInterruptedActionAsync(
            new RecoverCommissioningActionCommand(
                "session-a",
                "action.press",
                CommissioningActionIdempotencyClass.NonIdempotent));
        var replay = await service.ResolveRecoveryAsync(
            new ResolveCommissioningRecoveryCommand(
                "session-a",
                "engineer-a",
                CommissioningRecoveryDisposition.Replay));

        Assert.True(interrupted.IsSuccess);
        Assert.Equal(
            CommissioningSessionStatus.RecoveryRequired,
            interrupted.Value.Status);
        Assert.True(replay.IsFailure);
        Assert.Equal(
            "Conflict.Commissioning.NonIdempotentReplayForbidden",
            replay.Error.Code);
    }

    private static CommissioningService CreateService(
        out InMemoryCommissioningSessionRepository repository,
        out MutableClock clock)
    {
        repository = new InMemoryCommissioningSessionRepository();
        clock = new MutableClock(StartedAtUtc);
        var allCapabilities = Enum.GetValues<CommissioningCapability>().ToHashSet();
        var policy = new CommissioningRoleAccessPolicy(
            new Dictionary<string, IReadOnlySet<string>>
            {
                ["engineer-a"] = new HashSet<string>(StringComparer.Ordinal)
                {
                    "CommissioningEngineer"
                }
            },
            new Dictionary<string, IReadOnlySet<CommissioningCapability>>
            {
                ["CommissioningEngineer"] = allCapabilities
            });
        return new CommissioningService(repository, policy, clock);
    }

    private static StartCommissioningSessionCommand StartCommand(
        string sessionId,
        string stationId,
        IReadOnlyCollection<CommissioningCapability>? capabilities = null) =>
        new(
            sessionId,
            stationId,
            "engineer-a",
            "CommissioningEngineer",
            $"lease.{sessionId}",
            FencingToken: 41,
            Duration: TimeSpan.FromHours(2),
            capabilities ?? Enum.GetValues<CommissioningCapability>());

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}
