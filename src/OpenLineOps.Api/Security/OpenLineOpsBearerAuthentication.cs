using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OpenLineOps.Agent.Contracts;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Security;

public sealed class OpenLineOpsSecurityOptions
{
    public const string SectionName = "OpenLineOps:Security";

    public List<OpenLineOpsCallerCredentialOptions> Callers { get; init; } = [];
}

public sealed class OpenLineOpsCallerCredentialOptions
{
    public string CredentialId { get; init; } = string.Empty;

    public string ActorId { get; init; } = string.Empty;

    public string TokenSha256 { get; init; } = string.Empty;

    public List<string> Roles { get; init; } = [];

    public string? StationId { get; init; }
}

public sealed class OpenLineOpsSecurityOptionsValidator(IConfiguration? configuration = null)
    : IValidateOptions<OpenLineOpsSecurityOptions>
{
    private static readonly HashSet<string> AllowedRoles =
    [
        OpenLineOpsApiSecurity.EngineeringRole,
        OpenLineOpsApiSecurity.OperatorRole,
        OpenLineOpsApiSecurity.SafetyRole,
        OpenLineOpsApiSecurity.StationAgentRole
    ];

    public ValidateOptionsResult Validate(string? name, OpenLineOpsSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Callers is null || options.Callers.Count == 0)
        {
            failures.Add(
                $"{OpenLineOpsSecurityOptions.SectionName}:Callers must contain at least one explicitly provisioned caller credential.");
            return ValidateOptionsResult.Fail(failures);
        }

        var credentialIds = new HashSet<string>(StringComparer.Ordinal);
        var credentialPairs = new HashSet<string>(StringComparer.Ordinal);
        var tokenHashes = new HashSet<string>(StringComparer.Ordinal);
        var hasSafetyOnlyCredential = false;
        var hasStationAgentCredential = false;
        for (var index = 0; index < options.Callers.Count; index++)
        {
            var caller = options.Callers[index];
            var prefix = $"{OpenLineOpsSecurityOptions.SectionName}:Callers:{index}";
            if (caller is null)
            {
                failures.Add($"{prefix} cannot be null.");
                continue;
            }

            if (!IsCanonicalIdentifier(caller.CredentialId))
            {
                failures.Add($"{prefix}:CredentialId is not canonical.");
            }
            else if (!credentialIds.Add(caller.CredentialId))
            {
                failures.Add($"{prefix}:CredentialId is duplicated.");
            }

            if (!IsCanonicalIdentifier(caller.ActorId))
            {
                failures.Add($"{prefix}:ActorId is not canonical.");
            }

            if (IsCanonicalIdentifier(caller.CredentialId)
                && IsCanonicalIdentifier(caller.ActorId)
                && !credentialPairs.Add(
                    string.Concat(caller.ActorId, "\n", caller.CredentialId)))
            {
                failures.Add($"{prefix}:ActorId and CredentialId pair is duplicated.");
            }

            if (!IsCanonicalSha256(caller.TokenSha256))
            {
                failures.Add($"{prefix}:TokenSha256 must be one canonical lowercase SHA-256 digest.");
            }
            else if (!tokenHashes.Add(caller.TokenSha256))
            {
                failures.Add($"{prefix}:TokenSha256 is duplicated.");
            }

            if (caller.Roles is null || caller.Roles.Count == 0)
            {
                failures.Add($"{prefix}:Roles must contain at least one explicit role.");
                continue;
            }

            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var role in caller.Roles)
            {
                if (!AllowedRoles.Contains(role))
                {
                    failures.Add($"{prefix}:Roles contains unsupported role '{role}'.");
                }
                else if (!roles.Add(role))
                {
                    failures.Add($"{prefix}:Roles contains duplicate role '{role}'.");
                }
            }

            if (roles.Contains(OpenLineOpsApiSecurity.SafetyRole))
            {
                if (roles.Count != 1)
                {
                    failures.Add(
                        $"{prefix}:Safety must use a dedicated credential and cannot be combined with Engineering or Operator.");
                }
                else
                {
                    hasSafetyOnlyCredential = true;
                }
            }

            if (roles.Contains(OpenLineOpsApiSecurity.StationAgentRole))
            {
                if (roles.Count != 1)
                {
                    failures.Add(
                        $"{prefix}:StationAgent must use a dedicated credential and cannot be combined with another role.");
                }

                if (!StationIdentityContract.IsCanonical(caller.ActorId))
                {
                    failures.Add(
                        $"{prefix}:ActorId must satisfy the shared Station identity contract for a StationAgent credential.");
                }

                if (!StationIdentityContract.IsCanonical(caller.StationId))
                {
                    failures.Add($"{prefix}:StationId is required for a StationAgent credential.");
                }
                else
                {
                    hasStationAgentCredential = true;
                }
            }
            else if (caller.StationId is not null)
            {
                failures.Add($"{prefix}:StationId is allowed only for a StationAgent credential.");
            }
        }

        if (!hasSafetyOnlyCredential)
        {
            failures.Add(
                $"{OpenLineOpsSecurityOptions.SectionName}:Callers must contain at least one dedicated Safety-only credential.");
        }

        var stationExecutionProvider = configuration?[
            "OpenLineOps:Runtime:StationExecution:Provider"] ?? "Agent";
        var agentTransportProvider = configuration?[
            "OpenLineOps:Runtime:AgentTransport:Provider"] ?? "RabbitMq";
        if (string.Equals(stationExecutionProvider, "Agent", StringComparison.Ordinal)
            && string.Equals(agentTransportProvider, "RabbitMq", StringComparison.Ordinal)
            && !hasStationAgentCredential)
        {
            failures.Add(
                $"{OpenLineOpsSecurityOptions.SectionName}:Callers requires at least one dedicated StationAgent credential when Agent Station execution uses RabbitMq transport.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsCanonicalIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '@' or '/' or '-');

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 }
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class OpenLineOpsBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptionsMonitor<OpenLineOpsSecurityOptions> securityOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (authorizationValues.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.Fail("Exactly one Bearer credential is required."));
        }

        var authorization = authorizationValues[0];
        const string prefix = "Bearer ";
        if (authorization is null
            || !authorization.StartsWith(prefix, StringComparison.Ordinal)
            || authorization.Length == prefix.Length)
        {
            return Task.FromResult(AuthenticateResult.Fail("The Authorization header is invalid."));
        }

        var token = authorization[prefix.Length..];
        if (!IsCanonicalHighEntropyToken(token))
        {
            return Task.FromResult(AuthenticateResult.Fail("The Bearer credential is invalid."));
        }

        Span<byte> actualHash = stackalloc byte[32];
        _ = SHA256.HashData(Encoding.UTF8.GetBytes(token), actualHash);
        OpenLineOpsCallerCredentialOptions? matchedCaller = null;
        foreach (var caller in securityOptions.CurrentValue.Callers)
        {
            var expectedHash = Convert.FromHexString(caller.TokenSha256);

            if (CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                matchedCaller = caller;
            }
        }

        if (matchedCaller is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("The Bearer credential is invalid."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, matchedCaller.ActorId),
            new(ClaimTypes.Name, matchedCaller.ActorId),
            new("openlineops:credential_id", matchedCaller.CredentialId)
        };
        claims.AddRange(matchedCaller.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        if (matchedCaller.StationId is not null)
        {
            claims.Add(new Claim(OpenLineOpsApiSecurity.StationIdClaim, matchedCaller.StationId));
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            OpenLineOpsApiSecurity.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role));
        var ticket = new AuthenticationTicket(
            principal,
            OpenLineOpsApiSecurity.AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool IsCanonicalHighEntropyToken(string token)
    {
        if (token.Length is < 43 or > 86
            || token.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var padded = token.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            var bytes = Convert.FromBase64String(padded);
            return bytes.Length is >= 32 and <= 64
                && string.Equals(
                    Convert.ToBase64String(bytes)
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_'),
                    token,
                    StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class HttpsOrLoopbackMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.IsHttps && !IsLoopback(context))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    public static void ValidateConfiguredUrls(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredUrls = configuration["urls"];
        if (string.IsNullOrWhiteSpace(configuredUrls))
        {
            return;
        }

        foreach (var configuredUrl in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps
                    && (uri.Scheme != Uri.UriSchemeHttp || !IsLoopbackHost(uri.Host))))
            {
                throw new InvalidOperationException(
                    "OpenLineOps.Api may bind cleartext HTTP only to an explicit loopback host. Remote Coordinator endpoints require HTTPS.");
            }
        }
    }

    private static bool IsLoopback(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is not null)
        {
            return IPAddress.IsLoopback(remoteAddress);
        }

        return IsLoopbackHost(context.Request.Host.Host);
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
