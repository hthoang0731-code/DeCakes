using DeCakes.Application.Bakeries;

namespace DeCakes.Application.Tests.Bakeries;

public class BakeryDetailServiceTests
{
    private readonly FakeBakeryCatalogStore _store = new();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_id_is_rejected_before_store(long id)
    {
        var result = await new BakeryCatalogService(_store).GetByIdAsync(id, CancellationToken.None);

        Assert.Contains("id", Assert.IsType<BakeryDetailResult.ValidationFailed>(result).Errors.Keys);
        Assert.Empty(_store.DetailCalls);
    }

    [Fact]
    public async Task Public_bakery_is_returned_and_cancellation_is_propagated()
    {
        _store.Detail = new BakeryDetailDto(long.MaxValue, "Tiệm", "tiem", null, null, null, null);
        using var cancellation = new CancellationTokenSource();

        var result = await new BakeryCatalogService(_store).GetByIdAsync(long.MaxValue, cancellation.Token);

        Assert.Equal(_store.Detail, Assert.IsType<BakeryDetailResult.Found>(result).Bakery);
        Assert.Equal((long.MaxValue, cancellation.Token), Assert.Single(_store.DetailCalls));
    }

    [Fact]
    public async Task No_visible_bakery_is_not_found()
    {
        Assert.IsType<BakeryDetailResult.NotFound>(
            await new BakeryCatalogService(_store).GetByIdAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Store_failure_is_not_disguised_as_not_found()
    {
        _store.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BakeryCatalogService(_store).GetByIdAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BakeryCatalogService(_store).GetByIdAsync(1, cancellation.Token));
    }
}
