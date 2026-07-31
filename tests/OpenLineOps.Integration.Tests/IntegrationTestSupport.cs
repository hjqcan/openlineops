using Microsoft.Data.Sqlite;
using OpenLineOps.Integration.Application.Inbox;
using OpenLineOps.Integration.Application.Outbox;
using OpenLineOps.Integration.Domain.Identifiers;
using OpenLineOps.Integration.Domain.Messages;

namespace OpenLineOps.Integration.Tests;

internal sealed class TemporaryIntegrationDatabase : IDisposable
{
    private readonly string _directory;

    public TemporaryIntegrationDatabase()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "openlineops-integration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "integration.db"),
            Pooling = false
        };
        ConnectionString = builder.ToString();
    }

    public string ConnectionString { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

internal sealed class CountingWorkRequestHandler : IWorkRequestHandler
{
    private int _invocationCount;

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public ValueTask<WorkResponse> HandleAsync(
        WorkRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _invocationCount);
        return ValueTask.FromResult(IntegrationTestData.ResponseFor(request));
    }
}

internal sealed class BlockingWorkRequestHandler : IWorkRequestHandler
{
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _invocationCount;

    public Task Entered => _entered.Task;

    public int InvocationCount => Volatile.Read(ref _invocationCount);

    public void Release() => _release.TrySetResult();

    public async ValueTask<WorkResponse> HandleAsync(
        WorkRequest request,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _invocationCount);
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        return IntegrationTestData.ResponseFor(request);
    }
}

internal sealed class AlwaysFailingConnector : IIntegrationConnector
{
    private readonly List<string> _attemptedMessageIds = [];

    public IReadOnlyList<string> AttemptedMessageIds => _attemptedMessageIds;

    public ValueTask SendAsync(
        IntegrationOutboundMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _attemptedMessageIds.Add(message.MessageId);
        return ValueTask.FromException(
            new IOException("Enterprise network is unavailable."));
    }
}

internal sealed class FixedReplayAuthorizer(bool authorized) : IIntegrationReplayAuthorizer
{
    public ValueTask<bool> IsAuthorizedAsync(
        ManualOutboxReplayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(authorized);
    }
}

internal static class IntegrationTestData
{
    public static readonly DateTimeOffset Epoch =
        new(2026, 7, 31, 1, 2, 3, TimeSpan.Zero);

    public static WorkRequest Request(
        string requestId,
        string payloadJson = """{"model":"A","quantity":4}""",
        int minuteOffset = 0) =>
        new(
            new WorkRequestId(requestId),
            new WorkOrderId($"order-{requestId}"),
            WorkRequestKind.CreateOrUpdate,
            WorkRequestStatus.Received,
            "mes-primary",
            Epoch.AddMinutes(minuteOffset),
            payloadJson);

    public static WorkResponse ResponseFor(WorkRequest request) =>
        new(
            new WorkResponseId($"response-{request.Id.Value}"),
            request.Id,
            request.WorkOrderId,
            WorkResponseStatus.Accepted,
            request.SourceSystem,
            request.OccurredAtUtc.AddSeconds(1),
            """{"accepted":true}""");
}
