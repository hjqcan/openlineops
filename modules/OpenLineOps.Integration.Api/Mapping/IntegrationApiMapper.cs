using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using OpenLineOps.Integration.Api.Models;
using OpenLineOps.Integration.Application.Queries;
using OpenLineOps.Integration.Domain.Messages;
using OpenLineOps.Integration.Domain.WorkOrders;

namespace OpenLineOps.Integration.Api.Mapping;

internal static class IntegrationApiMapper
{
    public static WorkOrderResponse ToResponse(WorkOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return new WorkOrderResponse(
            order.Id.Value,
            order.ProductModelId,
            order.TargetQuantity,
            order.Status.ToString(),
            order.Facts.Select(static fact => new WorkOrderFactResponse(
                    fact.Id.Value,
                    fact.Sequence,
                    fact.Kind.ToString(),
                    fact.ResultingStatus.ToString(),
                    fact.OccurredAtUtc,
                    fact.ActorId,
                    fact.ProductModelId,
                    fact.TargetQuantity,
                    fact.Reason))
                .ToArray());
    }

    public static WorkRequestResponse ToResponse(
        IntegrationWorkRequestSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var request = snapshot.Request;
        return new WorkRequestResponse(
            request.Id.Value,
            request.WorkOrderId.Value,
            request.Kind.ToString(),
            request.Status.ToString(),
            request.SourceSystem,
            request.OccurredAtUtc,
            ParsePayload(request.PayloadJson),
            snapshot.ReceivedAtUtc,
            snapshot.State.ToString(),
            snapshot.ResponseMessageId,
            snapshot.CompletedAtUtc);
    }

    public static WorkResponseResponse ToResponse(
        IntegrationWorkResponseSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ToResponse(
            snapshot.Response,
            snapshot.Sequence,
            snapshot.AttemptCount,
            snapshot.NextAttemptAtUtc,
            snapshot.LastError,
            snapshot.DeliveredAtUtc,
            snapshot.DeadLetteredAtUtc);
    }

    public static WorkResponseResponse ToResponse(WorkResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ToResponse(
            response,
            sequence: 0,
            attemptCount: 0,
            nextAttemptAtUtc: response.OccurredAtUtc,
            lastError: null,
            deliveredAtUtc: null,
            deadLetteredAtUtc: null);
    }

    public static ProblemDetails Problem(
        int status,
        string title,
        string detail)
    {
        return new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };
    }

    private static WorkResponseResponse ToResponse(
        WorkResponse response,
        long sequence,
        int attemptCount,
        DateTimeOffset nextAttemptAtUtc,
        string? lastError,
        DateTimeOffset? deliveredAtUtc,
        DateTimeOffset? deadLetteredAtUtc)
    {
        return new WorkResponseResponse(
            response.Id.Value,
            response.RequestId.Value,
            response.WorkOrderId.Value,
            response.Status.ToString(),
            response.TargetSystem,
            response.OccurredAtUtc,
            ParsePayload(response.PayloadJson),
            sequence,
            attemptCount,
            nextAttemptAtUtc,
            lastError,
            deliveredAtUtc,
            deadLetteredAtUtc);
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }
}
