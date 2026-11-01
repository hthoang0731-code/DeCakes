namespace DeCakes.Application.Common;

/// <summary>Quy ước phân trang chung của API: page ≥ 1 (mặc định 1), pageSize 1–100 (mặc định 20).</summary>
public static class Paging
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Áp mặc định và ghi lỗi theo tên tham số query vào <paramref name="errors"/>.</summary>
    public static (int Page, int PageSize) Normalize(int? page, int? pageSize, IDictionary<string, string[]> errors)
    {
        var pageNumber = page ?? 1;
        if (pageNumber < 1)
        {
            errors["page"] = ["page phải lớn hơn hoặc bằng 1."];
        }

        var size = pageSize ?? DefaultPageSize;
        if (size < 1 || size > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize phải từ 1 đến {MaxPageSize}."];
        }

        return (pageNumber, size);
    }
}
