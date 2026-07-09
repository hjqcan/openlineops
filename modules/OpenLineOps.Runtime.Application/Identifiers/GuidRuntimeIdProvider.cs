using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Identifiers;

public sealed class GuidRuntimeIdProvider : IRuntimeIdProvider
{
    public RuntimeSessionId NewSessionId()
    {
        return RuntimeSessionId.New();
    }

    public RuntimeStepId NewStepId()
    {
        return RuntimeStepId.New();
    }

    public RuntimeCommandId NewCommandId()
    {
        return RuntimeCommandId.New();
    }
}
