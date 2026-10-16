using DeCakes.Application.Cakes;

namespace DeCakes.Application.Tests.Cakes;

public class CakeCatalogServiceTests
{
    private readonly FakeCakeCatalogStore _store = new();

    [Theory]
    [InlineData(null, null, null, 0L, 21)]
    [InlineData(3, 2, 7L, 4L, 3)]
    [InlineData(int.MaxValue, 100, long.MaxValue, 214748364600L, 101)]
    public async Task Valid_list_normalizes_paging_and_passes_filter_and_cancellation(
        int? page, int? size, long? bakeryId, long offset, int take)
    {
        using var cancellation = new CancellationTokenSource();
        var result = await new CakeCatalogService(_store).ListAsync(page, size, bakeryId, cancellation.Token);

        var response = Assert.IsType<CakeListResult.Succeeded>(result).Page;
        Assert.Equal(page ?? 1, response.Page);
        Assert.Equal(size ?? 20, response.PageSize);
        Assert.Empty(response.Items);
        Assert.False(response.HasNext);
        Assert.Equal((offset, take, bakeryId, cancellation.Token), Assert.Single(_store.ListCalls));
    }

    [Theory]
    [InlineData(0, 20, null, "page")]
    [InlineData(-1, 20, null, "page")]
    [InlineData(1, 0, null, "pageSize")]
    [InlineData(1, 101, null, "pageSize")]
    [InlineData(1, 20, 0L, "bakeryId")]
    [InlineData(1, 20, -1L, "bakeryId")]
    public async Task Invalid_list_does_not_reach_store(int page, int size, long? bakeryId, string field)
    {
        var result = await new CakeCatalogService(_store).ListAsync(page, size, bakeryId, CancellationToken.None);

        Assert.Contains(field, Assert.IsType<CakeListResult.ValidationFailed>(result).Errors.Keys);
        Assert.Empty(_store.ListCalls);
    }

    [Fact]
    public async Task All_invalid_list_fields_are_reported_together()
    {
        var result = await new CakeCatalogService(_store).ListAsync(0, 101, 0, CancellationToken.None);
        Assert.Equal(3, Assert.IsType<CakeListResult.ValidationFailed>(result).Errors.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task List_trims_lookahead_and_preserves_nullable_price(int count, bool hasNext)
    {
        _store.Rows = Enumerable.Range(1, count).Select(i => new CakeListItemDto(i, 1, "Bánh", null, "VND")).ToArray();

        var result = Assert.IsType<CakeListResult.Succeeded>(
            await new CakeCatalogService(_store).ListAsync(1, 2, null, CancellationToken.None)).Page;

        Assert.Equal(_store.Rows.Take(2), result.Items);
        Assert.Equal(hasNext, result.HasNext);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_detail_id_does_not_reach_store(long id)
    {
        var result = await new CakeCatalogService(_store).GetByIdAsync(id, CancellationToken.None);
        Assert.Contains("id", Assert.IsType<CakeDetailResult.ValidationFailed>(result).Errors.Keys);
        Assert.Empty(_store.DetailCalls);
    }

    [Fact]
    public async Task Detail_preserves_dto_and_passes_large_id_and_token()
    {
        _store.Detail = new CakeDetailDto(long.MaxValue, 2, "Bánh", 123456.78m, "VND", null, "Vani", null);
        using var cancellation = new CancellationTokenSource();

        var result = await new CakeCatalogService(_store).GetByIdAsync(long.MaxValue, cancellation.Token);

        Assert.Equal(_store.Detail, Assert.IsType<CakeDetailResult.Found>(result).Cake);
        Assert.Equal((long.MaxValue, cancellation.Token), Assert.Single(_store.DetailCalls));
    }

    [Fact]
    public async Task Missing_or_nonpublic_detail_is_not_found()
    {
        Assert.IsType<CakeDetailResult.NotFound>(await new CakeCatalogService(_store).GetByIdAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Failures_are_not_converted_to_empty_or_not_found()
    {
        _store.Fail = true;
        var service = new CakeCatalogService(_store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync(1, 20, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetByIdAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_propagated_for_both_operations()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new CakeCatalogService(_store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(1, 20, null, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetByIdAsync(1, cancellation.Token));
    }
}
