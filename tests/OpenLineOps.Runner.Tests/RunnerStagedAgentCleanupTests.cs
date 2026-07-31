using System.Net;
using Npgsql;
using OpenLineOps.Agent.Contracts;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace OpenLineOps.Runner.Tests;

public sealed class RunnerStagedAgentCleanupTests
{
    private const string EnabledVariable = "OPENLINEOPS_RUNNER_AGENT_CLEANUP_ENABLED";
    private const string PostgreSqlVariable = "OPENLINEOPS_RUNNER_AGENT_CLEANUP_POSTGRES_CONNECTION_STRING";
    private const string RabbitMqVariable = "OPENLINEOPS_RUNNER_AGENT_CLEANUP_RABBITMQ_URI";
    private const string ScopeVariable = "OPENLINEOPS_RUNNER_AGENT_CLEANUP_SCOPE_ID";
    private static readonly TimeSpan BoundaryTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CleanupIsolatedPostgreSqlSchemaAndRabbitMqQueues()
    {
        var prerequisites = ResolvePrerequisites();
        if (prerequisites is null)
        {
            return;
        }

        var failures = new List<Exception>();
        try
        {
            await DropPostgreSqlSchemaAsync(
                prerequisites.PostgreSqlConnectionString,
                $"olo_runner_agent_{prerequisites.ScopeId}");
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await DeleteRabbitMqQueuesAsync(
                prerequisites.RabbitMqUri,
                prerequisites.ScopeId);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(failures[0])
                .Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Runner staged-Agent outer compensation did not complete.",
                failures);
        }
    }

    private static CleanupPrerequisites? ResolvePrerequisites()
    {
        var enabled = Environment.GetEnvironmentVariable(EnabledVariable);
        var postgres = Environment.GetEnvironmentVariable(PostgreSqlVariable);
        var rabbit = Environment.GetEnvironmentVariable(RabbitMqVariable);
        var scope = Environment.GetEnvironmentVariable(ScopeVariable);
        if (enabled is null && postgres is null && rabbit is null && scope is null)
        {
            return null;
        }

        if (!string.Equals(enabled, "1", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(postgres)
            || string.IsNullOrWhiteSpace(rabbit)
            || scope is not { Length: 32 }
            || scope.Any(static character =>
                character is not (>= 'a' and <= 'f')
                && character is not (>= '0' and <= '9')))
        {
            throw new InvalidDataException(
                "Runner staged-Agent compensation requires its complete explicit opt-in environment.");
        }

        if (!Uri.TryCreate(rabbit, UriKind.Absolute, out var rabbitUri)
            || rabbitUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidDataException(
                "Runner staged-Agent compensation requires an absolute amqp or amqps URI.");
        }

        if (rabbitUri.Scheme == "amqp"
            && !Dns.GetHostAddresses(rabbitUri.DnsSafeHost).All(IPAddress.IsLoopback))
        {
            throw new InvalidDataException(
                "Cleartext RabbitMQ compensation is restricted to loopback.");
        }

        return new CleanupPrerequisites(postgres, rabbitUri, scope);
    }

    private static async Task DropPostgreSqlSchemaAsync(
        string connectionString,
        string schema)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = string.Empty,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        using var timeout = new CancellationTokenSource(BoundaryTimeout);
        try
        {
            await connection.OpenAsync(timeout.Token);
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;",
                connection)
            {
                CommandTimeout = 5
            };
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static async Task DeleteRabbitMqQueuesAsync(Uri brokerUri, string scopeId)
    {
        var factory = new ConnectionFactory
        {
            Uri = brokerUri,
            ClientProvidedName = $"openlineops-runner-agent-cleanup-{scopeId}",
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
            RequestedHeartbeat = TimeSpan.FromSeconds(5),
            HandshakeContinuationTimeout = TimeSpan.FromSeconds(5),
            ContinuationTimeout = TimeSpan.FromSeconds(5),
            SocketReadTimeout = TimeSpan.FromSeconds(5),
            SocketWriteTimeout = TimeSpan.FromSeconds(5)
        };
        var agentId = $"agent-{scopeId}";
        var stationId = $"station-{scopeId}";
        var coordinatorId = $"runner-agent-{scopeId}";
        var queues = new[]
        {
            StationTransportRoute.JobQueue(agentId, stationId),
            $"openlineops.coordinator.{coordinatorId}.station-results",
            StationTransportRoute.SafetyQueue(agentId, stationId, "emergency-stop"),
            StationTransportRoute.SafetyQueue(agentId, stationId, "safe-stop"),
            StationTransportRoute.SafetyQueue(agentId, stationId, "job-cancel")
        };

        var connection = await factory.CreateConnectionAsync().WaitAsync(BoundaryTimeout);
        try
        {
            foreach (var queue in queues)
            {
                var channel = await connection.CreateChannelAsync().WaitAsync(BoundaryTimeout);
                try
                {
                    try
                    {
                        await channel.QueueDeleteAsync(
                                queue,
                                ifUnused: false,
                                ifEmpty: false)
                            .WaitAsync(BoundaryTimeout);
                    }
                    catch (OperationInterruptedException exception)
                        when (exception.ShutdownReason?.ReplyCode == 404)
                    {
                    }
                }
                finally
                {
                    await channel.DisposeAsync().AsTask().WaitAsync(BoundaryTimeout);
                }
            }
        }
        finally
        {
            await connection.DisposeAsync().AsTask().WaitAsync(BoundaryTimeout);
        }
    }

    private sealed record CleanupPrerequisites(
        string PostgreSqlConnectionString,
        Uri RabbitMqUri,
        string ScopeId);
}
