namespace RealEstate.Application.Common;

public sealed record Error(string Code, string Message);

public class Result
{
    public bool Succeeded { get; }
    public Error? Error { get; }
    protected Result(bool ok, Error? error) { Succeeded = ok; Error = error; }
    public static Result Success() => new(true, null);
    public static Result Failure(string code, string message) => new(false, new Error(code, message));
}

public sealed class Result<T> : Result
{
    public T? Value { get; }
    private Result(T? value, bool ok, Error? error) : base(ok, error) => Value = value;
    public static Result<T> Success(T value) => new(value, true, null);
    public new static Result<T> Failure(string code, string message) => new(default, false, new Error(code, message));
}
