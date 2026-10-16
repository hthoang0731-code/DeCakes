using DeCakes.Application.Bakeries;

namespace DeCakes.Application.Tests.Bakeries;

public class BakeryCatalogServiceTests
{
    private readonly FakeBakeryCatalogStore _store = new();

    [Theory]
    [InlineData(null, null, 1, 20, 0L, 21)]
    [InlineData(3, 2, 3, 2, 4L, 3)]
    [InlineData(null, 100, 1, 100, 0L, 101)]
    [InlineData(int.MaxValue, 100, int.MaxValue, 100, 214748364600L, 101)]
    public async Task Valid_paging_defaults_and_long_offsets_reach_store(
        int? page, int? pageSize, int expectedPage, int expectedPageSize, long expectedOffset, int expectedTake)
    {
        var service = new BakeryCatalogService(_store);

        var result = Assert.IsType<BakeryListResult.Succeeded>(await service.ListAsync(page, pageSize, CancellationToken.None));

        Assert.Equal(expectedPage, result.Page.Page);
        Assert.Equal(expectedPageSize, result.Page.PageSize);
        Assert.Empty(result.Page.Items);
        Assert.False(result.Page.HasNext);
        Assert.Equal((expectedOffset, expectedTake, CancellationToken.None), Assert.Single(_store.Calls));
    }

    [Theory]
    [InlineData(0, 20, "page")]
    [InlineData(-1, 20, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, -1, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    public async Task Invalid_paging_does_not_call_store(int page, int pageSize, string field)
    {
        var result = await new BakeryCatalogService(_store).ListAsync(page, pageSize, CancellationToken.None);

        Assert.Contains(field, Assert.IsType<BakeryListResult.ValidationFailed>(result).Errors.Keys);
        Assert.Empty(_store.Calls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task Extra_row_sets_has_next_without_leaking_into_items(int count, bool expectedHasNext)
    {
        _store.Rows = Enumerable.Range(1, count).Select(i => new BakeryListItemDto(i, $"Tiệm {i}", $"tiem-{i}", null)).ToArray();

        var result = Assert.IsType<BakeryListResult.Succeeded>(
            await new BakeryCatalogService(_store).ListAsync(1, 2, CancellationToken.None));

        Assert.Equal(_store.Rows.Take(2), result.Page.Items);
        Assert.Equal(expectedHasNext, result.Page.HasNext);
    }

    [Fact]
    public async Task Store_failure_is_not_converted_to_an_empty_page()
    {
        _store.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new BakeryCatalogService(_store).ListAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_reaches_the_store_and_is_not_swallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BakeryCatalogService(_store).ListAsync(1, 20, cancellation.Token));
        Assert.Equal(cancellation.Token, Assert.Single(_store.Calls).CancellationToken);
    }
}
