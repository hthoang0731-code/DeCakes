using DeCakes.Application.Bakeries;

namespace DeCakes.Application.Tests.Bakeries;

internal sealed class FakeBakeryCatalogStore : IBakeryCatalogStore
{
    public IReadOnlyList<BakeryListItemDto> Rows { get; set; } = [];

    public List<(long Offset, int Take, CancellationToken CancellationToken)> Calls { get; } = [];

    public bool Fail { get; set; }

    public BakeryDetailDto? Detail { get; set; }

    public List<(long Id, CancellationToken CancellationToken)> DetailCalls { get; } = [];

    public Task<BakeryDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken)
    {
        DetailCalls.Add((id, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("SIMULATED-STORE-FAILURE");
        return Task.FromResult(Detail);
    }

    public Task<IReadOnlyList<BakeryListItemDto>> ListPublicAsync(long offset, int take, CancellationToken cancellationToken)
    {
        Calls.Add((offset, take, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("SIMULATED-STORE-FAILURE");
        return Task.FromResult(Rows);
    }
}
