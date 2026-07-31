using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Agent.Application.StationController;

namespace OpenLineOps.Agent.Infrastructure.Transport;

public sealed class HttpStationControllerCoordinatorClient(HttpClient httpClient) :
    IStationControllerCoordinatorClient
{
    public const string LeaseHandleHeaderName = "X-OpenLineOps-Agent-Lease";

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async ValueTask<StationAgentControlLeaseGrant> AcquireLeaseAsync(
        StationAgentProcessIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{LeaseRoute(identity.StationId)}/acquire")
        {
            Content = JsonContent.Create(
                new AcquireLeaseRequest(identity.OwnerInstanceId),
                options: JsonOptions)
        };
        request.Headers.Add(LeaseHandleHeaderName, identity.LeaseHandle);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureLeaseResponseAsync(response, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        var stationId = RequiredString(root, "stationId");
        var ownerAgentId = RequiredString(root, "ownerAgentId");
        var ownerInstanceId = RequiredString(root, "ownerInstanceId");
        if (!string.Equals(stationId, identity.StationId, StringComparison.Ordinal)
            || !string.Equals(
                ownerAgentId,
                identity.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                ownerInstanceId,
                identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || !RequiredBoolean(root, "active"))
        {
            throw new InvalidDataException(
                "Coordinator returned a control lease for another Agent identity "
                + "or an inactive lease.");
        }

        return new StationAgentControlLeaseGrant(
            stationId,
            ownerInstanceId,
            RequiredInt64(root, "fencingToken"),
            RequiredUtc(root, "expiresAtUtc"),
            identity.LeaseHandle);
    }

    public async ValueTask<DateTimeOffset> RenewLeaseAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity, lease);
        using var request = AuthorizedLeaseRequest(
            HttpMethod.Post,
            $"{LeaseRoute(identity.StationId)}/renew",
            lease,
            new RenewLeaseRequest(
                identity.OwnerInstanceId,
                lease.FencingToken));
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureLeaseResponseAsync(response, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (RequiredInt64(root, "fencingToken") != lease.FencingToken
            || !string.Equals(
                RequiredString(root, "stationId"),
                identity.StationId,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(root, "ownerAgentId"),
                identity.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(root, "ownerInstanceId"),
                identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || !RequiredBoolean(root, "active"))
        {
            throw new InvalidDataException(
                "Coordinator changed the control lease identity during renewal.");
        }

        return RequiredUtc(root, "expiresAtUtc");
    }

    public async ValueTask ReleaseLeaseAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity, lease);
        using var request = AuthorizedLeaseRequest(
            HttpMethod.Post,
            $"{LeaseRoute(identity.StationId)}/release",
            lease,
            new ReleaseLeaseRequest(
                identity.OwnerInstanceId,
                lease.FencingToken));
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureLeaseResponseAsync(response, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (RequiredInt64(root, "fencingToken") != lease.FencingToken
            || !string.Equals(
                RequiredString(root, "stationId"),
                identity.StationId,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(root, "ownerAgentId"),
                identity.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                RequiredString(root, "ownerInstanceId"),
                identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || RequiredBoolean(root, "active"))
        {
            throw new InvalidDataException(
                "Coordinator did not release the exact calling control lease.");
        }
    }

    public async ValueTask<StationControllerCommandEnvelope?> PollCommandAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity, lease);
        var uri = $"{LifecycleRoute(identity.StationId)}/controller-command"
            + $"?ownerInstanceId={Uri.EscapeDataString(identity.OwnerInstanceId)}"
            + $"&fencingToken={lease.FencingToken}";
        using var request = AuthorizedLeaseRequest<object>(
            HttpMethod.Get,
            uri,
            lease,
            body: null);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureLeaseResponseAsync(response, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (!string.Equals(
                RequiredString(root, "stationId"),
                identity.StationId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Coordinator returned a controller command for another Station.");
        }

        var command = RequiredObject(root, "command");
        var fencingToken = RequiredInt64(command, "fencingToken");
        var ownerAgentId = RequiredString(command, "ownerAgentId");
        var ownerInstanceId = RequiredString(command, "ownerAgentInstanceId");
        if (fencingToken != lease.FencingToken
            || !string.Equals(
                ownerAgentId,
                identity.AgentId,
                StringComparison.Ordinal)
            || !string.Equals(
                ownerInstanceId,
                identity.OwnerInstanceId,
                StringComparison.Ordinal))
        {
            throw new StationAgentControlLeaseRejectedException(
                "Coordinator returned a controller command for another Agent "
                + "owner or lease generation.");
        }

        return new StationControllerCommandEnvelope(
            identity.StationId,
            ownerAgentId,
            ownerInstanceId,
            RequiredString(command, "commandId"),
            RequiredString(command, "controllerSessionId"),
            RequiredInt64(command, "expectedCommandSequence"),
            fencingToken,
            RequiredString(command, "trigger"),
            RequiredString(command, "expectedMode"),
            RequiredString(command, "expectedCompletionState"),
            ParseIdempotency(RequiredString(command, "idempotency")),
            RequiredString(command, "safetyClass"),
            OptionalString(command, "confirmedRecipeId"),
            OptionalString(command, "confirmedRecipeVersion"),
            RequiredUtc(command, "issuedAtUtc"),
            RequiredUtc(command, "deadlineUtc"),
            RequiredInt64(command, "issuedOperationalEpoch"),
            OptionalString(command, "recipeAssignmentId"),
            OptionalString(command, "recipeDeploymentId"),
            OptionalString(command, "recipeConfigurationSha256"));
    }

    public async ValueTask ReportHandshakeAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        StationControllerHandshakeReport report,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity, lease);
        ArgumentNullException.ThrowIfNull(report);
        if (!string.Equals(
                report.OwnerInstanceId,
                identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || report.AgentFencingToken != lease.FencingToken
            || report.CommandFencingToken != lease.FencingToken)
        {
            throw new StationAgentControlLeaseRejectedException(
                "Controller handshake report is not bound to the calling lease.");
        }

        using var request = AuthorizedLeaseRequest(
            HttpMethod.Post,
            $"{LifecycleRoute(identity.StationId)}/controller-handshake",
            lease,
            report);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureLeaseResponseAsync(response, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask AcknowledgeCommandAsync(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease,
        StationControllerCommandAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity, lease);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!string.Equals(
                acknowledgement.OwnerInstanceId,
                identity.OwnerInstanceId,
                StringComparison.Ordinal)
            || acknowledgement.FencingToken != lease.FencingToken)
        {
            throw new StationAgentControlLeaseRejectedException(
                "Controller command acknowledgement is not bound to the calling lease.");
        }

        using var request = AuthorizedLeaseRequest(
            HttpMethod.Post,
            $"{LifecycleRoute(identity.StationId)}/commands/acknowledge",
            lease,
            acknowledgement);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        await EnsureLeaseResponseAsync(response, cancellationToken)
            .ConfigureAwait(false);
    }

    private static HttpRequestMessage AuthorizedLeaseRequest<TBody>(
        HttpMethod method,
        string uri,
        StationAgentControlLeaseGrant lease,
        TBody? body)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(LeaseHandleHeaderName, lease.LeaseHandle);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return request;
    }

    private static ValueTask EnsureLeaseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Conflict
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound)
        {
            return ValueTask.FromException(
                new StationAgentControlLeaseRejectedException(
                $"Coordinator rejected Station control authority with HTTP "
                + $"{(int)response.StatusCode}."));
        }

        if (!response.IsSuccessStatusCode)
        {
            _ = cancellationToken;
            return ValueTask.FromException(new HttpRequestException(
                $"Coordinator Station control request failed with HTTP "
                + $"{(int)response.StatusCode}.",
                inner: null,
                response.StatusCode));
        }

        return ValueTask.CompletedTask;
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static string LeaseRoute(string stationId) =>
        $"api/stations/{Uri.EscapeDataString(stationId)}/agent-control-lease";

    private static string LifecycleRoute(string stationId) =>
        $"api/stations/{Uri.EscapeDataString(stationId)}/lifecycle";

    private static void ValidateIdentity(
        StationAgentProcessIdentity identity,
        StationAgentControlLeaseGrant lease)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(identity.StationId, lease.StationId, StringComparison.Ordinal)
            || !string.Equals(
                identity.OwnerInstanceId,
                lease.OwnerInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                identity.LeaseHandle,
                lease.LeaseHandle,
                StringComparison.Ordinal))
        {
            throw new StationAgentControlLeaseRejectedException(
                "Control lease does not belong to the calling Agent process.");
        }
    }

    private static StationControllerCommandIdempotency ParseIdempotency(
        string value) =>
        Enum.TryParse<StationControllerCommandIdempotency>(
            value,
            ignoreCase: false,
            out var parsed)
        && Enum.IsDefined(parsed)
        && string.Equals(parsed.ToString(), value, StringComparison.Ordinal)
            ? parsed
            : throw new InvalidDataException(
                $"Coordinator returned unsupported command idempotency '{value}'.");

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Coordinator response requires object property '{name}'.");
        }

        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Coordinator response requires string property '{name}'.");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new InvalidDataException(
                $"Coordinator response property '{name}' must be text or null.");
    }

    private static long RequiredInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || !value.TryGetInt64(out var parsed)
            || parsed <= 0)
        {
            throw new InvalidDataException(
                $"Coordinator response requires positive integer property '{name}'.");
        }

        return parsed;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Coordinator response requires Boolean property '{name}'.");
        }

        return value.GetBoolean();
    }

    private static DateTimeOffset RequiredUtc(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || !value.TryGetDateTimeOffset(out var parsed)
            || parsed == default
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Coordinator response requires UTC timestamp property '{name}'.");
        }

        return parsed;
    }

    private sealed record AcquireLeaseRequest(string OwnerInstanceId);

    private sealed record RenewLeaseRequest(
        string OwnerInstanceId,
        long FencingToken);

    private sealed record ReleaseLeaseRequest(
        string OwnerInstanceId,
        long FencingToken);
}
