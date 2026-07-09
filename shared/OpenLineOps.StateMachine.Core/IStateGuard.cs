namespace OpenLineOps.StateMachine.Core;

public interface IStateGuard<TState, TTrigger>
    where TState : notnull
    where TTrigger : notnull
{
    ValueTask<bool> CanTransitionAsync(
        TState fromState,
        TState toState,
        TTrigger trigger,
        CancellationToken cancellationToken = default);
}
