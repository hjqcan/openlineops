using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Tests;

public sealed class OpenLineOpsApiWebApplicationFactory : StationPackageWebApplicationFactory
{
    public HttpClient CreateAuthenticatedClient(
        WebApplicationFactoryClientOptions? options = null,
        string? token = null)
    {
        var client = CreateClient(options ?? new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        ApiTestAuthentication.Authenticate(client, token);
        return client;
    }
}

internal static class ApiTestAuthentication
{
    public static readonly string StandardToken = Token("standard");

    public static readonly string EngineeringToken = Token("engineering");

    public static readonly string OperatorToken = Token("operator");

    public static readonly string SafetyToken = Token("safety");

    public static readonly string StationAgentToken = Token("station-agent");

    public const string StandardActorId = "test.studio";

    public const string EngineeringActorId = "test.engineer";

    public const string OperatorActorId = "test.operator";

    public const string SafetyActorId = "test.safety";

    public const string StationAgentActorId = "agent.test";

    public const string StationAgentStationId = "station.test";

    public static OpenLineOpsApiWebApplicationFactory CreateFactory() => new();

    public static void Configure(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(Settings()));
    }

    public static void Authenticate(HttpClient client, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token ?? StandardToken);
    }

    public static HttpClient CreateAuthenticatedClient(
        this WebApplicationFactory<Program> factory,
        WebApplicationFactoryClientOptions? options = null,
        string? token = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient(options ?? new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        Authenticate(client, token);
        return client;
    }

    public static WebApplicationFactory<Program> WithApiAuthentication(
        this WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(Configure);

    private static Dictionary<string, string?> Settings()
    {
        var callers = new[]
        {
            new Caller(
                "test-standard",
                StandardActorId,
                StandardToken,
                [OpenLineOpsApiSecurity.EngineeringRole, OpenLineOpsApiSecurity.OperatorRole]),
            new Caller(
                "test-engineering",
                EngineeringActorId,
                EngineeringToken,
                [OpenLineOpsApiSecurity.EngineeringRole]),
            new Caller(
                "test-operator",
                OperatorActorId,
                OperatorToken,
                [OpenLineOpsApiSecurity.OperatorRole]),
            new Caller(
                "test-safety",
                SafetyActorId,
                SafetyToken,
                [OpenLineOpsApiSecurity.SafetyRole]),
            new Caller(
                "test-station-agent",
                StationAgentActorId,
                StationAgentToken,
                [OpenLineOpsApiSecurity.StationAgentRole],
                StationAgentStationId)
        };
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < callers.Length; index++)
        {
            var prefix = $"OpenLineOps:Security:Callers:{index}";
            var caller = callers[index];
            settings[$"{prefix}:CredentialId"] = caller.CredentialId;
            settings[$"{prefix}:ActorId"] = caller.ActorId;
            settings[$"{prefix}:TokenSha256"] = Sha256(caller.Token);
            if (caller.StationId is not null)
            {
                settings[$"{prefix}:StationId"] = caller.StationId;
            }
            for (var roleIndex = 0; roleIndex < caller.Roles.Length; roleIndex++)
            {
                settings[$"{prefix}:Roles:{roleIndex}"] = caller.Roles[roleIndex];
            }
        }

        return settings;
    }

    private static string Token(string purpose) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"OpenLineOps.Api.Tests:{purpose}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Sha256(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private sealed record Caller(
        string CredentialId,
        string ActorId,
        string Token,
        string[] Roles,
        string? StationId = null);
}
