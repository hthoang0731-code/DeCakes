using DeCakes.Application.Catalog;

namespace DeCakes.Application.Bakeries;

public interface IBakeryCatalogService
{
    Task<BakeryListResult> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken);

    Task<BakeryDetailResult> GetByIdAsync(long id, CancellationToken cancellationToken);
}

public abstract record BakeryListResult
{
    public sealed record Succeeded(CatalogPage<BakeryListItemDto> Page) : BakeryListResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : BakeryListResult;
}

public abstract record BakeryDetailResult
{
    public sealed record Found(BakeryDetailDto Bakery) : BakeryDetailResult;

    public sealed record NotFound : BakeryDetailResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : BakeryDetailResult;
}

public sealed class BakeryCatalogService(IBakeryCatalogStore store) : IBakeryCatalogService
{
    public async Task<BakeryListResult> ListAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        int currentPage = page ?? CatalogPaging.DefaultPage;
        int currentPageSize = pageSize ?? CatalogPaging.DefaultPageSize;
        var errors = CatalogPaging.Validate(currentPage, currentPageSize);
        if (errors.Count > 0)
        {
            return new BakeryListResult.ValidationFailed(errors);
        }

        var rows = await store.ListPublicAsync(
            CatalogPaging.GetOffset(currentPage, currentPageSize), currentPageSize + 1, cancellationToken);
        return new BakeryListResult.Succeeded(new CatalogPage<BakeryListItemDto>(
            rows.Take(currentPageSize).ToArray(), currentPage, currentPageSize, rows.Count > currentPageSize));
    }

    public async Task<BakeryDetailResult> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return new BakeryDetailResult.ValidationFailed(new Dictionary<string, string[]>
            {
                ["id"] = ["Id phải là số nguyên dương."],
            });
        }

        var bakery = await store.FindPublicByIdAsync(id, cancellationToken);
        return bakery is null ? new BakeryDetailResult.NotFound() : new BakeryDetailResult.Found(bakery);
    }
}
