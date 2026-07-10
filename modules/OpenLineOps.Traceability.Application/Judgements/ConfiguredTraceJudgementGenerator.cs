using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Domain.Abstractions.Serialization;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Judgements;

public sealed class ConfiguredTraceJudgementGenerator : ITraceJudgementGenerator
{
    private readonly TraceJudgementOptions _options;

    public ConfiguredTraceJudgementGenerator(TraceJudgementOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Result<ResultJudgement> Generate(CreateTraceRecordRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Judgement is not null)
        {
            return ParseJudgement(request.Judgement, "Traceability.InvalidJudgement");
        }

        var measurements = (request.Stages ?? [])
            .SelectMany(stage => stage.Measurements ?? [])
            .ToArray();

        if (_options.FailWhenAnyMeasurementFailed
            && measurements.Any(measurement => measurement.Passed == false))
        {
            return Result.Success(ResultJudgement.Failed);
        }

        if (_options.UnknownWhenAnyMeasurementIndeterminate
            && measurements.Any(measurement => measurement.Passed is null))
        {
            return Result.Success(ResultJudgement.Unknown);
        }

        if (_options.UnknownWhenNoMeasurements
            && measurements.Length == 0)
        {
            return Result.Success(ResultJudgement.Unknown);
        }

        return ParseJudgement(_options.DefaultJudgement, "Traceability.InvalidDefaultJudgement");
    }

    private static Result<ResultJudgement> ParseJudgement(string value, string errorCode)
    {
        if (CanonicalEnumToken.TryParse<ResultJudgement>(value, out var parsed))
        {
            return Result.Success(parsed);
        }

        return Result.Failure<ResultJudgement>(ApplicationError.Validation(
            errorCode,
            $"Result judgement '{value}' is not supported. Expected an exact, case-sensitive token: " +
            $"{CanonicalEnumToken.ExpectedTokens<ResultJudgement>()}."));
    }
}
