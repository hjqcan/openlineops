namespace OpenLineOps.StateMachine.Core;

public interface IStateMachine<TState, TTrigger>
    where TState : notnull
    where TTrigger : notnull
{
    TState CurrentState { get; }

    ValueTask<StateTransitionResult<TState>> FireAsync(
        TTrigger trigger,
        CancellationToken cancellationToken = default);
}
