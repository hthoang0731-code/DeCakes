using DeCakes.Application.Catalog;

namespace DeCakes.Application.Cakes;

public interface ICakeCatalogService
{
    Task<CakeListResult> ListAsync(int? page, int? pageSize, long? bakeryId, CancellationToken cancellationToken);

    Task<CakeDetailResult> GetByIdAsync(long id, CancellationToken cancellationToken);
}

public abstract record CakeListResult
{
    public sealed record Succeeded(CatalogPage<CakeListItemDto> Page) : CakeListResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : CakeListResult;
}

public abstract record CakeDetailResult
{
    public sealed record Found(CakeDetailDto Cake) : CakeDetailResult;

    public sealed record NotFound : CakeDetailResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : CakeDetailResult;
}

public sealed class CakeCatalogService(ICakeCatalogStore store) : ICakeCatalogService
{
    public async Task<CakeListResult> ListAsync(int? page, int? pageSize, long? bakeryId, CancellationToken cancellationToken)
    {
        int currentPage = page ?? CatalogPaging.DefaultPage;
        int currentPageSize = pageSize ?? CatalogPaging.DefaultPageSize;
        var errors = CatalogPaging.Validate(currentPage, currentPageSize).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (bakeryId is <= 0)
        {
            errors["bakeryId"] = ["Id tiệm bánh phải là số nguyên dương."];
        }

        if (errors.Count > 0) return new CakeListResult.ValidationFailed(errors);

        var rows = await store.ListPublicAsync(
            CatalogPaging.GetOffset(currentPage, currentPageSize), currentPageSize + 1, bakeryId, cancellationToken);
        return new CakeListResult.Succeeded(new CatalogPage<CakeListItemDto>(
            rows.Take(currentPageSize).ToArray(), currentPage, currentPageSize, rows.Count > currentPageSize));
    }

    public async Task<CakeDetailResult> GetByIdAsync(long id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return new CakeDetailResult.ValidationFailed(new Dictionary<string, string[]>
            {
                ["id"] = ["Id phải là số nguyên dương."],
            });
        }

        var cake = await store.FindPublicByIdAsync(id, cancellationToken);
        return cake is null ? new CakeDetailResult.NotFound() : new CakeDetailResult.Found(cake);
    }
}
