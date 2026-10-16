using DeCakes.Application.Bakeries;

namespace DeCakes.Api.Tests.Infrastructure;

public sealed class InMemoryBakeryCatalogStore : IBakeryCatalogStore
{
    public List<BakeryListItemDto> Items { get; } = [];

    public int Calls { get; private set; }

    public bool Fail { get; set; }

    public Dictionary<long, BakeryDetailDto> Details { get; } = [];

    public Task<BakeryDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("SIMULATED-CATALOG-FAILURE");
        return Task.FromResult(Details.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<BakeryListItemDto>> ListPublicAsync(long offset, int take, CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("SIMULATED-CATALOG-FAILURE");
        IReadOnlyList<BakeryListItemDto> rows = offset >= Items.Count
            ? []
            : Items.OrderBy(item => item.Id).Skip((int)offset).Take(take).ToArray();
        return Task.FromResult(rows);
    }
}
