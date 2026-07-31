using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenLineOps.Api.Abstractions;

namespace OpenLineOps.Api.Security;

public static class OpenLineOpsAuthenticationServiceCollectionExtensions
{
    private static readonly HashSet<string> OidcHumanRoles =
    [
        OpenLineOpsApiSecurity.EngineeringRole,
        OpenLineOpsApiSecurity.OperatorRole,
        OpenLineOpsApiSecurity.SafetyRole
    ];

    public static IServiceCollection AddOpenLineOpsAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<
            IValidateOptions<OpenLineOpsSecurityOptions>,
            OpenLineOpsSecurityOptionsValidator>();
        services
            .AddOptions<OpenLineOpsSecurityOptions>()
            .Bind(configuration.GetSection(OpenLineOpsSecurityOptions.SectionName))
            .ValidateOnStart();

        _ = services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme =
                    OpenLineOpsApiSecurity.AuthenticationScheme;
                options.DefaultChallengeScheme =
                    OpenLineOpsApiSecurity.AuthenticationScheme;
            })
            .AddPolicyScheme(
                OpenLineOpsApiSecurity.AuthenticationScheme,
                OpenLineOpsApiSecurity.AuthenticationScheme,
                options => options.ForwardDefaultSelector = context =>
                    context.RequestServices
                        .GetRequiredService<
                            IOptionsMonitor<OpenLineOpsSecurityOptions>>()
                        .CurrentValue
                        .Oidc
                        .Enabled
                    && HasCompactJwtBearer(context.Request)
                        ? OpenLineOpsApiSecurity.OidcAuthenticationScheme
                        : OpenLineOpsApiSecurity.StaticAuthenticationScheme)
            .AddScheme<
                AuthenticationSchemeOptions,
                OpenLineOpsBearerAuthenticationHandler>(
                OpenLineOpsApiSecurity.StaticAuthenticationScheme,
                static _ => { })
            .AddJwtBearer(
                OpenLineOpsApiSecurity.OidcAuthenticationScheme,
                static _ => { });
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>>(
            serviceProvider => new OpenLineOpsOidcJwtBearerOptionsSetup(
                serviceProvider.GetRequiredService<
                    IOptionsMonitor<OpenLineOpsSecurityOptions>>()));

        return services;
    }

    private static bool HasCompactJwtBearer(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values)
            || values.Count != 1)
        {
            return false;
        }

        var authorization = values[0];
        const string prefix = "Bearer ";
        if (authorization is null
            || !authorization.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var token = authorization[prefix.Length..];
        return token.Count(static character => character == '.') == 2;
    }

    private static Task MapValidatedOidcIdentity(
        TokenValidatedContext context,
        OpenLineOpsOidcOptions options)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            context.Fail("OIDC token did not produce a claims identity.");
            return Task.CompletedTask;
        }

        var actorId = identity.FindFirst(options.ActorIdClaimType)?.Value;
        if (!IsCanonicalIdentity(actorId))
        {
            context.Fail("OIDC token actor identity is missing or invalid.");
            return Task.CompletedTask;
        }

        AddClaimIfMissing(identity, ClaimTypes.NameIdentifier, actorId!);
        AddClaimIfMissing(identity, ClaimTypes.Name, actorId!);

        foreach (var externalRole in identity
                     .FindAll(options.RoleClaimType)
                     .Select(static claim => claim.Value)
                     .Distinct(StringComparer.Ordinal)
                     .ToArray())
        {
            var role = options.RoleMappings.Count == 0
                ? externalRole
                : options.RoleMappings.GetValueOrDefault(externalRole);
            if (role is not null && OidcHumanRoles.Contains(role))
            {
                AddClaimIfMissing(identity, ClaimTypes.Role, role);
                AddClaimIfMissing(identity, identity.RoleClaimType, role);
            }
        }

        if (options.StationIdClaimType is not null)
        {
            var stationId = identity.FindFirst(options.StationIdClaimType)?.Value;
            if (stationId is not null)
            {
                if (!IsCanonicalIdentity(stationId))
                {
                    context.Fail("OIDC token Station identity is invalid.");
                    return Task.CompletedTask;
                }

                AddClaimIfMissing(
                    identity,
                    OpenLineOpsApiSecurity.StationIdClaim,
                    stationId);
            }
        }

        return Task.CompletedTask;
    }

    private static void AddClaimIfMissing(
        ClaimsIdentity identity,
        string type,
        string value)
    {
        if (!identity.HasClaim(type, value))
        {
            identity.AddClaim(new Claim(type, value));
        }
    }

    private static bool IsCanonicalIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or ':' or '@' or '/' or '-');

    private sealed class OpenLineOpsOidcJwtBearerOptionsSetup(
        IOptionsMonitor<OpenLineOpsSecurityOptions> securityOptions)
        : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(JwtBearerOptions options) =>
            Configure(Options.DefaultName, options);

        public void Configure(string? name, JwtBearerOptions options)
        {
            if (!string.Equals(
                    name,
                    OpenLineOpsApiSecurity.OidcAuthenticationScheme,
                    StringComparison.Ordinal))
            {
                return;
            }

            var oidc = securityOptions.CurrentValue.Oidc;
            options.Authority = oidc.Authority;
            options.Audience = oidc.Audience;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.SaveToken = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = ClaimTypes.Role
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                    MapValidatedOidcIdentity(context, oidc)
            };
        }
    }
}
