namespace OpenLineOps.Application.Abstractions.Results;

public sealed record ApplicationError(string Code, string Message)
{
    public static readonly ApplicationError None = new(string.Empty, string.Empty);

    public static ApplicationError Validation(string code, string message)
    {
        return new($"Validation.{code}", message);
    }

    public static ApplicationError Conflict(string code, string message)
    {
        return new($"Conflict.{code}", message);
    }

    public static ApplicationError NotFound(string code, string message)
    {
        return new($"NotFound.{code}", message);
    }
}
