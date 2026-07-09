using OpenLineOps.Application.Abstractions.Results;
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

        if (!string.IsNullOrWhiteSpace(request.Judgement))
        {
            return ParseJudgement(request.Judgement, "Traceability.InvalidJudgement");
        }

        var measurements = request.Measurements ?? [];

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
            && measurements.Count == 0)
        {
            return Result.Success(ResultJudgement.Unknown);
        }

        return ParseJudgement(_options.DefaultJudgement, "Traceability.InvalidDefaultJudgement");
    }

    private static Result<ResultJudgement> ParseJudgement(string value, string errorCode)
    {
        if (Enum.TryParse<ResultJudgement>(value, ignoreCase: true, out var parsed))
        {
            return Result.Success(parsed);
        }

        return Result.Failure<ResultJudgement>(ApplicationError.Validation(
            errorCode,
            $"Result judgement '{value}' is not supported."));
    }
}
