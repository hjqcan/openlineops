namespace OpenLineOps.Infrastructure.Data.Core.EventBus;

public enum IntegrationEventPublicationMode
{
    PostCommit,
    Transactional
}

public sealed record IntegrationEventPublicationPolicy(IntegrationEventPublicationMode Mode);

public static class IntegrationEventPublicationModes
{
    public const string PostCommit = nameof(IntegrationEventPublicationMode.PostCommit);

    public const string Transactional = nameof(IntegrationEventPublicationMode.Transactional);

    public static IntegrationEventPublicationMode Parse(string? value)
    {
        return value switch
        {
            PostCommit => IntegrationEventPublicationMode.PostCommit,
            Transactional => IntegrationEventPublicationMode.Transactional,
            _ => throw new InvalidOperationException(
                $"Integration event publication mode must be explicitly configured as '{PostCommit}' or '{Transactional}'.")
        };
    }
}
