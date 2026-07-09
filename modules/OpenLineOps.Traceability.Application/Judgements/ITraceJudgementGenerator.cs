using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Application.Records;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.Judgements;

public interface ITraceJudgementGenerator
{
    Result<ResultJudgement> Generate(CreateTraceRecordRequest request);
}
