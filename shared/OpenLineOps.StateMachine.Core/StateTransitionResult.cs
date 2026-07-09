namespace OpenLineOps.StateMachine.Core;

public sealed record StateTransitionResult<TState>(
    bool Succeeded,
    TState FromState,
    TState ToState,
    string? Reason = null)
    where TState : notnull;

public static class StateTransitionResult
{
    public static StateTransitionResult<TState> Success<TState>(TState fromState, TState toState)
        where TState : notnull
    {
        return new StateTransitionResult<TState>(true, fromState, toState);
    }

    public static StateTransitionResult<TState> Rejected<TState>(TState currentState, string reason)
        where TState : notnull
    {
        return new StateTransitionResult<TState>(false, currentState, currentState, reason);
    }
}
