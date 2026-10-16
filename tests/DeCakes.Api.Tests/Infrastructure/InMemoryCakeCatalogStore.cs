using DeCakes.Application.Cakes;

namespace DeCakes.Api.Tests.Infrastructure;

public sealed class InMemoryCakeCatalogStore : ICakeCatalogStore
{
    public List<CakeListItemDto> Items { get; } = [];
    public Dictionary<long, CakeDetailDto> Details { get; } = [];
    public int Calls { get; private set; }
    public bool Fail { get; set; }

    public Task<IReadOnlyList<CakeListItemDto>> ListPublicAsync(long offset, int take, long? bakeryId, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        var filtered = Items.Where(item => !bakeryId.HasValue || item.BakeryId == bakeryId.Value).OrderBy(item => item.Id).ToArray();
        IReadOnlyList<CakeListItemDto> rows = offset >= filtered.Length ? [] : filtered.Skip((int)offset).Take(take).ToArray();
        return Task.FromResult(rows);
    }

    public Task<CakeDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        return Task.FromResult(Details.GetValueOrDefault(id));
    }

    private void Check(CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("SIMULATED-CATALOG-FAILURE");
    }
}
