namespace AnomalyDetection.Application.Common;

public enum OperationStatus
{
    Ok,
    NotFound,
    Conflict,
    Invalid,
    Unavailable,
}

/// <summary>Service-layer result so controllers stay thin and map outcomes to HTTP status codes.</summary>
public sealed record OperationResult<T>(OperationStatus Status, T? Value, string? Error)
{
    public bool IsOk => Status == OperationStatus.Ok;

    public static OperationResult<T> Ok(T value) => new(OperationStatus.Ok, value, null);

    public static OperationResult<T> NotFound(string error) => new(OperationStatus.NotFound, default, error);

    public static OperationResult<T> Conflict(string error) => new(OperationStatus.Conflict, default, error);

    public static OperationResult<T> Invalid(string error) => new(OperationStatus.Invalid, default, error);

    public static OperationResult<T> Unavailable(string error) => new(OperationStatus.Unavailable, default, error);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long Total);

public static class Paging
{
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize, int defaultPageSize = 25)
    {
        var p = page is null or < 1 ? 1 : page.Value;
        var s = pageSize is null or < 1 ? defaultPageSize : Math.Min(pageSize.Value, MaxPageSize);
        return (p, s);
    }
}
