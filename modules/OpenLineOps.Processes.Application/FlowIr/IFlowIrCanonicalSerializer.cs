using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.FlowIr;

public interface IFlowIrCanonicalSerializer
{
    Result<FlowIrCanonicalArtifact> Serialize(FlowIrDocument document);

    Result<FlowIrDocument> Deserialize(string canonicalJson);
}
