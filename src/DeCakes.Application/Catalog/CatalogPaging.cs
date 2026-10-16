namespace DeCakes.Application.Catalog;

public sealed record CatalogPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, bool HasNext);

public static class CatalogPaging
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public static IReadOnlyDictionary<string, string[]> Validate(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["Trang phải lớn hơn hoặc bằng 1."];
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            errors["pageSize"] = [$"Kích thước trang phải từ 1 đến {MaxPageSize}."];
        }

        return errors;
    }

    public static long GetOffset(int page, int pageSize) => ((long)page - 1) * pageSize;
}
