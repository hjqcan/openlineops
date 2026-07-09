namespace OpenLineOps.Processes.Domain.Nodes;

public enum ProcessNodeKind
{
    Start = 0,
    Command = 1,
    Decision = 2,
    Delay = 3,
    End = 4,
    PythonScript = 5
}
