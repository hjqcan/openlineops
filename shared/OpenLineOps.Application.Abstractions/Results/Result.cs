namespace OpenLineOps.Application.Abstractions.Results;

public class Result
{
    protected Result(bool isSuccess, ApplicationError error)
    {
        if (isSuccess && error != ApplicationError.None)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error == ApplicationError.None)
        {
            throw new ArgumentException("A failed result must carry an error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public ApplicationError Error { get; }

    public static Result Success()
    {
        return new Result(true, ApplicationError.None);
    }

    public static Result Failure(ApplicationError error)
    {
        return new Result(false, error);
    }

    public static Result<T> Success<T>(T value)
    {
        return new Result<T>(value);
    }

    public static Result<T> Failure<T>(ApplicationError error)
    {
        return new Result<T>(error);
    }
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T value)
        : base(true, ApplicationError.None)
    {
        _value = value;
    }

    internal Result(ApplicationError error)
        : base(false, error)
    {
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("The value of a failed result cannot be accessed.");
}
