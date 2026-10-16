using DeCakes.Application.Cakes;

namespace DeCakes.Application.Tests.Cakes;

internal sealed class FakeCakeCatalogStore : ICakeCatalogStore
{
    public IReadOnlyList<CakeListItemDto> Rows { get; set; } = [];
    public CakeDetailDto? Detail { get; set; }
    public bool Fail { get; set; }
    public List<(long Offset, int Take, long? BakeryId, CancellationToken Token)> ListCalls { get; } = [];
    public List<(long Id, CancellationToken Token)> DetailCalls { get; } = [];

    public Task<IReadOnlyList<CakeListItemDto>> ListPublicAsync(long offset, int take, long? bakeryId, CancellationToken cancellationToken)
    {
        ListCalls.Add((offset, take, bakeryId, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("SIMULATED-STORE-FAILURE");
        return Task.FromResult(Rows);
    }

    public Task<CakeDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken)
    {
        DetailCalls.Add((id, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("SIMULATED-STORE-FAILURE");
        return Task.FromResult(Detail);
    }
}
