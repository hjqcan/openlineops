using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenLineOps.Api.Tests;

public sealed class QualityApiTests : IClassFixture<OpenLineOpsApiWebApplicationFactory>
{
    private static readonly DateTimeOffset BaseTimeUtc =
        new(2026, 7, 31, 2, 0, 0, TimeSpan.Zero);
    private readonly OpenLineOpsApiWebApplicationFactory _factory;

    public QualityApiTests(OpenLineOpsApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task QualityWorkflowUsesClaimBoundStationAndAppendOnlyDisposition()
    {
        using var engineering = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        using var stationAgent = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.StationAgentToken);
        using var operatorClient = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);

        var testPlanRevisionId = Guid.NewGuid();
        var testPlanId = Guid.NewGuid();
        var characteristicId = Guid.NewGuid();
        var calibrationAssetId = Guid.NewGuid();
        var testAttemptId = Guid.NewGuid();
        var measurementResultId = Guid.NewGuid();
        var productionUnitId = $"unit-{testAttemptId:N}";

        using var planResponse = await PostIdempotentAsync(
            engineering,
            "/api/quality/test-plans",
            $"plan-{testPlanRevisionId:N}",
            new
            {
                testPlanRevisionId,
                testPlanId,
                revisionNumber = 1,
                displayName = "Functional test",
                characteristics = new[]
                {
                    new
                    {
                        testCharacteristicId = characteristicId,
                        code = "supply.voltage",
                        displayName = "Supply voltage",
                        unit = "V",
                        stepVersion = "step-voltage@1",
                        isRequired = true,
                        limits = new
                        {
                            limitSetId = Guid.NewGuid(),
                            unit = "V",
                            lowerLimit = 4.5m,
                            lowerInclusive = true,
                            upperLimit = 5.5m,
                            upperInclusive = true
                        }
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Created, planResponse.StatusCode);

        using var calibrationResponse = await PostIdempotentAsync(
            engineering,
            "/api/quality/calibration-assets",
            $"calibration-{calibrationAssetId:N}",
            new
            {
                calibrationAssetId,
                assetCode = $"asset-{calibrationAssetId:N}",
                instrumentId = $"instrument-{calibrationAssetId:N}",
                certificateId = $"certificate-{calibrationAssetId:N}",
                calibratedAtUtc = BaseTimeUtc.AddDays(-1),
                validUntilUtc = BaseTimeUtc.AddDays(30)
            });
        Assert.Equal(HttpStatusCode.Created, calibrationResponse.StatusCode);

        using var attemptResponse = await PostIdempotentAsync(
            stationAgent,
            "/api/quality/attempts",
            $"attempt-{testAttemptId:N}",
            new
            {
                testAttemptId,
                testPlanRevisionId,
                productionUnitId,
                attemptNumber = 1,
                startedAtUtc = BaseTimeUtc
            });
        Assert.Equal(HttpStatusCode.Created, attemptResponse.StatusCode);
        using (var attemptDocument = await JsonDocument.ParseAsync(
                   await attemptResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(
                ApiTestAuthentication.StationAgentStationId,
                attemptDocument.RootElement.GetProperty("stationId").GetString());
            Assert.Equal(
                ApiTestAuthentication.StationAgentActorId,
                attemptDocument.RootElement.GetProperty("createdBy").GetString());
        }

        using var measurementResponse = await PostIdempotentAsync(
            stationAgent,
            $"/api/quality/attempts/{testAttemptId:D}/measurements",
            $"measurement-{measurementResultId:N}",
            new
            {
                expectedRevision = 1,
                measurementResultId,
                testCharacteristicId = characteristicId,
                rawValue = "6.0 V",
                normalizedValue = 6m,
                calibrationAssetId,
                stepVersion = "step-voltage@1",
                evidenceSha256 = new string('a', 64),
                measuredAtUtc = BaseTimeUtc.AddSeconds(1)
            });
        Assert.Equal(HttpStatusCode.OK, measurementResponse.StatusCode);
        using (var measurementDocument = await JsonDocument.ParseAsync(
                   await measurementResponse.Content.ReadAsStreamAsync()))
        {
            var measurement = measurementDocument.RootElement
                .GetProperty("measurements")
                .EnumerateArray()
                .Single();
            Assert.Equal("Failed", measurement.GetProperty("judgement").GetString());
            Assert.Equal(2, measurementDocument.RootElement.GetProperty("resourceRevision").GetInt32());
        }

        using var completionResponse = await PostIdempotentAsync(
            stationAgent,
            $"/api/quality/attempts/{testAttemptId:D}/completion",
            $"completion-{testAttemptId:N}",
            new
            {
                expectedRevision = 2,
                completedAtUtc = BaseTimeUtc.AddSeconds(2)
            });
        Assert.Equal(HttpStatusCode.OK, completionResponse.StatusCode);
        using (var completionDocument = await JsonDocument.ParseAsync(
                   await completionResponse.Content.ReadAsStreamAsync()))
        {
            Assert.Equal("Completed", completionDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("Failed", completionDocument.RootElement.GetProperty("judgement").GetString());
        }

        using var queryResponse = await operatorClient.GetAsync(
            $"/api/quality/nonconformances?stationId={ApiTestAuthentication.StationAgentStationId}&status=Open");
        Assert.Equal(HttpStatusCode.OK, queryResponse.StatusCode);
        using var queryDocument = await JsonDocument.ParseAsync(
            await queryResponse.Content.ReadAsStreamAsync());
        var nonconformance = queryDocument.RootElement
            .EnumerateArray()
            .Single(item =>
                item.GetProperty("testAttemptId").GetGuid() == testAttemptId);
        var nonconformanceId = nonconformance.GetProperty("nonconformanceId").GetGuid();
        Assert.Equal("Major", nonconformance.GetProperty("severity").GetString());

        using var dispositionResponse = await PostIdempotentAsync(
            operatorClient,
            $"/api/quality/nonconformances/{nonconformanceId:D}/disposition",
            $"disposition-{nonconformanceId:N}",
            new
            {
                expectedRevision = 1,
                disposition = "Rework",
                reason = "Inspect the connector and repeat once."
            });
        Assert.Equal(HttpStatusCode.OK, dispositionResponse.StatusCode);
        using var dispositionDocument = await JsonDocument.ParseAsync(
            await dispositionResponse.Content.ReadAsStreamAsync());
        Assert.Equal(
            "Dispositioned",
            dispositionDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            ApiTestAuthentication.OperatorActorId,
            dispositionDocument.RootElement.GetProperty("dispositionedBy").GetString());
    }

    [Fact]
    public async Task QualityEndpointsRequireTheirNamedRolePolicies()
    {
        using var anonymous = _factory.CreateClient();
        using var operatorClient = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.OperatorToken);
        using var engineering = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        using var stationAgent = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.StationAgentToken);

        using var anonymousResponse = await anonymous.GetAsync("/api/quality/test-plans");
        using var operatorEngineeringResponse = await operatorClient.GetAsync("/api/quality/test-plans");
        using var engineeringOperationsResponse = await engineering.GetAsync(
            $"/api/quality/attempts/{Guid.NewGuid():D}");
        using var stationOperationsResponse = await stationAgent.GetAsync(
            $"/api/quality/attempts/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorEngineeringResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, engineeringOperationsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stationOperationsResponse.StatusCode);
    }

    [Fact]
    public async Task TestPlanIdempotencyRejectsChangedRequestAndUnknownMembers()
    {
        using var engineering = _factory.CreateAuthenticatedClient(
            token: ApiTestAuthentication.EngineeringToken);
        var revisionId = Guid.NewGuid();
        var characteristicId = Guid.NewGuid();
        var idempotencyKey = $"plan-idempotency-{revisionId:N}";
        var request = new
        {
            testPlanRevisionId = revisionId,
            testPlanId = Guid.NewGuid(),
            revisionNumber = 1,
            displayName = "Functional test",
            characteristics = new[]
            {
                new
                {
                    testCharacteristicId = characteristicId,
                    code = "supply.voltage",
                    displayName = "Supply voltage",
                    unit = "V",
                    stepVersion = "step-voltage@1",
                    isRequired = true,
                    limits = new
                    {
                        limitSetId = Guid.NewGuid(),
                        unit = "V",
                        lowerLimit = 4.5m,
                        lowerInclusive = true,
                        upperLimit = 5.5m,
                        upperInclusive = true
                    }
                }
            }
        };

        using var first = await PostIdempotentAsync(
            engineering,
            "/api/quality/test-plans",
            idempotencyKey,
            request);
        using var replay = await PostIdempotentAsync(
            engineering,
            "/api/quality/test-plans",
            idempotencyKey,
            request);
        using var conflict = await PostIdempotentAsync(
            engineering,
            "/api/quality/test-plans",
            idempotencyKey,
            new
            {
                request.testPlanRevisionId,
                request.testPlanId,
                request.revisionNumber,
                displayName = "Changed evidence",
                request.characteristics
            });
        using var unknownMember = await PostIdempotentAsync(
            engineering,
            "/api/quality/calibration-assets",
            $"unknown-member-{Guid.NewGuid():N}",
            new
            {
                calibrationAssetId = Guid.NewGuid(),
                assetCode = "asset-unknown-member",
                instrumentId = "instrument-unknown-member",
                certificateId = "certificate-unknown-member",
                calibratedAtUtc = BaseTimeUtc.AddDays(-1),
                validUntilUtc = BaseTimeUtc.AddDays(1),
                actorId = "spoofed.actor"
            });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownMember.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostIdempotentAsync<T>(
        HttpClient client,
        string requestUri,
        string idempotencyKey,
        T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }
}
