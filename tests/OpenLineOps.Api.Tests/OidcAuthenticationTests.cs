using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Api.Security;

namespace OpenLineOps.Api.Tests;

public sealed class OidcAuthenticationTests(
    OpenLineOpsApiWebApplicationFactory baseFactory)
    : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    [Fact]
    public async Task OidcBearerMapsExternalRoleAndAuthenticatedActor()
    {
        const string issuer = "https://identity.example/tenant";
        const string audience = "api://openlineops";
        var signingKey = new SymmetricSecurityKey(
            RandomNumberGenerator.GetBytes(32))
        {
            KeyId = "oidc-api-test-key"
        };
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["OpenLineOps:Security:Oidc:Enabled"] = "true",
                        ["OpenLineOps:Security:Oidc:Authority"] = issuer,
                        ["OpenLineOps:Security:Oidc:Audience"] = audience,
                        ["OpenLineOps:Security:Oidc:ActorIdClaimType"] = "sub",
                        ["OpenLineOps:Security:Oidc:RoleClaimType"] = "roles",
                        ["OpenLineOps:Security:Oidc:RoleMappings:line-engineers"] =
                            OpenLineOpsApiSecurity.EngineeringRole
                    }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(
                    OpenLineOpsApiSecurity.OidcAuthenticationScheme,
                    options =>
                    {
                        var configuration = new OpenIdConnectConfiguration
                        {
                            Issuer = issuer,
                            SigningKeys = { signingKey }
                        };
                        options.Configuration = configuration;
                        options.ConfigurationManager =
                            new StaticConfigurationManager<
                                OpenIdConnectConfiguration>(configuration);
                    });
            });
        });
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
        var schemes = await factory.Services
            .GetRequiredService<IAuthenticationSchemeProvider>()
            .GetAllSchemesAsync();
        Assert.Contains(
            schemes,
            scheme => string.Equals(
                scheme.Name,
                OpenLineOpsApiSecurity.OidcAuthenticationScheme,
                StringComparison.Ordinal));
        var security = factory.Services
            .GetRequiredService<IOptions<OpenLineOpsSecurityOptions>>()
            .Value;
        Assert.Equal(
            OpenLineOpsApiSecurity.EngineeringRole,
            security.Oidc.RoleMappings["line-engineers"]);
        var token = CreateToken(issuer, audience, signingKey);
        var parsedToken = new JsonWebTokenHandler().ReadJsonWebToken(token);
        Assert.Contains(
            parsedToken.Claims,
            claim => claim.Type == "roles"
                     && claim.Value == "line-engineers");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                token);
        using var authenticationProbe = await client.GetAsync("/api/platform");
        Assert.Equal(HttpStatusCode.Forbidden, authenticationProbe.StatusCode);
        var suffix = Guid.NewGuid().ToString("N");

        using var created = await client.PostAsJsonAsync(
            "/api/integration/work-orders",
            new
            {
                workOrderId = $"oidc-order-{suffix}",
                productModelId = "model-oidc",
                targetQuantity = 1,
                factId = $"oidc-fact-{suffix}",
                occurredAtUtc = DateTimeOffset.UtcNow
            });
        var responseBody = await created.Content.ReadAsStringAsync();
        Assert.True(
            created.StatusCode == HttpStatusCode.Created,
            $"Expected 201 Created, received {(int)created.StatusCode}: {responseBody}");
        using var document = JsonDocument.Parse(responseBody);

        Assert.Equal(
            "oidc.engineer",
            document.RootElement
                .GetProperty("facts")[0]
                .GetProperty("actorId")
                .GetString());
    }

    private static string CreateToken(
        string issuer,
        string audience,
        SecurityKey key)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", "oidc.engineer"),
                new Claim("roles", "line-engineers")
            ]),
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                key,
                SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

}
