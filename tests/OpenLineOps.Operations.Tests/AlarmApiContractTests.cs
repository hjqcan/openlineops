using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using OpenLineOps.Api.Abstractions;
using OpenLineOps.Operations.Api.Controllers;
using OpenLineOps.Operations.Api.Models;

namespace OpenLineOps.Operations.Tests;

public sealed class AlarmApiContractTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(nameof(AlarmsController.RegisterDefinition), OpenLineOpsApiSecurity.EngineeringPolicy)]
    [InlineData(nameof(AlarmsController.Raise), OpenLineOpsApiSecurity.StationAgentPolicy)]
    [InlineData(nameof(AlarmsController.Acknowledge), OpenLineOpsApiSecurity.OperatorPolicy)]
    [InlineData(nameof(AlarmsController.Shelf), OpenLineOpsApiSecurity.OperatorPolicy)]
    [InlineData(nameof(AlarmsController.Suppress), OpenLineOpsApiSecurity.EngineeringPolicy)]
    [InlineData(nameof(AlarmsController.ClearSource), OpenLineOpsApiSecurity.StationAgentPolicy)]
    [InlineData(nameof(AlarmsController.GetFacts), OpenLineOpsApiSecurity.OperatorPolicy)]
    public void MutatingAndAuditEndpointsUseLeastPrivilegePolicy(
        string methodName,
        string expectedPolicy)
    {
        var method = typeof(AlarmsController).GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance);

        var authorization = Assert.Single(
            method!.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(expectedPolicy, authorization.Policy);
    }

    [Fact]
    public void AlarmApiModelsRejectUnknownJsonProperties()
    {
        const string payload = """
            {
              "comment": "Reviewed.",
              "commandId": "alarm-ack-api-1",
              "expectedVersion": 1,
              "unexpected": true
            }
            """;

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<AcknowledgeAlarmApiRequest>(
                payload,
                JsonOptions));
    }
}
