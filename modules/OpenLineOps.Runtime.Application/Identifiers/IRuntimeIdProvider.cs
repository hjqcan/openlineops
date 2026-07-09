using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Identifiers;

public interface IRuntimeIdProvider
{
    RuntimeSessionId NewSessionId();

    RuntimeStepId NewStepId();

    RuntimeCommandId NewCommandId();
}
