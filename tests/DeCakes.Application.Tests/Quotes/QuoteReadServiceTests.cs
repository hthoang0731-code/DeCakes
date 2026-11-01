using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using DeCakes.Domain.Quotes;

namespace DeCakes.Application.Tests.Quotes;

public class QuoteReadServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    internal static QuoteDto Quote(string status = QuoteStatuses.Submitted) => new(
        9, 7, 3, "Tiệm", 1, status, 680_000m, "VND", "20 cm", 10, "Vani", "Trang trí hoa", null, null,
        Now.AddDays(2), Now, null, null, Now.AddDays(-1), Now.AddDays(-1));

    [Theory]
    [InlineData(QuoteStatuses.Submitted, true)]
    [InlineData(QuoteStatuses.Accepted, true)]
    [InlineData(QuoteStatuses.Superseded, false)]
    [InlineData(QuoteStatuses.Withdrawn, false)]
    [InlineData(QuoteStatuses.NotSelected, false)]
    public async Task Expiry_is_computed_without_rewriting_status_or_history(string status, bool current)
    {
        var store = new Stub { Detail = new QuoteReadResult(QuoteReadOutcome.Found, Quote(status)) };
        var clock = new Clock(Now.AddMilliseconds(-1));
        var service = new QuoteService(store, clock);

        var before = await service.GetForCustomerAsync(4, 7, 9, CancellationToken.None);
        Assert.False(before.Quote!.IsExpired);
        clock.Now = Now;
        var atBoundary = await service.GetForCustomerAsync(4, 7, 9, CancellationToken.None);
        Assert.True(atBoundary.Quote!.IsExpired);
        Assert.Equal(status, atBoundary.Quote.Status);
        Assert.Equal(current, atBoundary.Quote.IsCurrent);
        Assert.False(store.Detail.Quote!.IsExpired);
        Assert.Equal((4L, 0L, 7L, 9L), store.Scope);
    }

    [Fact]
    public async Task Bakery_scope_and_cancellation_are_forwarded()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Stub();
        var service = new QuoteService(store, new Clock(Now));

        Assert.Equal(QuoteReadOutcome.QuoteNotFound,
            (await service.GetForBakeryAsync(4, 3, 7, 9, cancellation.Token)).Outcome);
        Assert.Equal((4L, 3L, 7L, 9L), store.Scope);
        Assert.Equal(cancellation.Token, store.Token);
    }

    [Theory]
    [InlineData("expired", 1, 20)]
    [InlineData("SUBMITTED", 1, 20)]
    [InlineData(null, 0, 20)]
    [InlineData(null, 1, 0)]
    [InlineData(null, 1, 101)]
    public async Task Invalid_filters_are_rejected_before_queries(string? status, int page, int pageSize)
    {
        var store = new Stub();
        var service = new QuoteService(store, new Clock(Now));

        Assert.IsType<ListQuotesResult.ValidationFailed>(await service.ListForCustomerAsync(4, 7, status, page, pageSize, CancellationToken.None));
        Assert.IsType<ListQuotesResult.ValidationFailed>(await service.ListForBakeryAsync(4, 3, 7, status, page, pageSize, CancellationToken.None));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task List_preserves_paging_and_uses_one_time_for_the_page()
    {
        var store = new Stub { List = new QuoteListResult(QuoteReadOutcome.Found, new PagedResult<QuoteDto>(
            [Quote(), Quote() with { Id = 10, ValidUntil = Now.AddMilliseconds(1) }], 2, 3, 9)) };
        var clock = new Clock(Now);
        var service = new QuoteService(store, clock);

        var result = Assert.IsType<ListQuotesResult.Ok>(await service.ListForBakeryAsync(4, 3, 7, " submitted ", 2, 3, CancellationToken.None));
        var data = result.Result.Page!;
        Assert.Equal((4L, 3L, 7L, 0L), store.Scope);
        Assert.Equal(("submitted", 2, 3), store.Filter);
        Assert.Equal((2, 3, 9L), (data.Page, data.PageSize, data.TotalCount));
        Assert.True(data.Items[0].IsExpired);
        Assert.False(data.Items[1].IsExpired);
        Assert.Equal(1, clock.Reads);
    }

    [Fact]
    public async Task Defaults_and_not_found_are_preserved()
    {
        var store = new Stub { List = new QuoteListResult(QuoteReadOutcome.RequestNotFound) };
        var service = new QuoteService(store, new Clock(Now));

        var result = Assert.IsType<ListQuotesResult.Ok>(await service.ListForCustomerAsync(4, 7, " ", null, null, CancellationToken.None));
        Assert.Equal(QuoteReadOutcome.RequestNotFound, result.Result.Outcome);
        Assert.Null(result.Result.Page);
        Assert.Equal(((string?)null, 1, 20), store.Filter);
    }

    [Fact]
    public async Task Unexpected_database_failure_is_not_converted_to_not_found()
    {
        var store = new Stub { Throw = true };
        var service = new QuoteService(store, new Clock(Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetForCustomerAsync(4, 7, 9, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListForBakeryAsync(4, 3, 7, null, null, null, CancellationToken.None));
    }

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return new DateTimeOffset(Now); }
    }

    private sealed class Stub : IQuoteStore
    {
        public QuoteReadResult Detail { get; init; } = new(QuoteReadOutcome.QuoteNotFound);
        public QuoteListResult List { get; init; } = new(QuoteReadOutcome.Found, new PagedResult<QuoteDto>([], 1, 20, 0));
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public (long Actor, long Bakery, long Request, long Quote) Scope { get; private set; }
        public (string? Status, int Page, int Size) Filter { get; private set; }
        public CancellationToken Token { get; private set; }

        private void Capture(long actor, long bakery, long request, long quote, CancellationToken token)
        {
            Calls++;
            Scope = (actor, bakery, request, quote);
            Token = token;
            if (Throw) throw new InvalidOperationException("Database unavailable");
        }
        public Task<QuoteReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken)
        { Capture(actorUserId, bakeryId, requestId, quoteId, cancellationToken); return Task.FromResult(Detail); }
        public Task<QuoteReadResult> FindForCustomerAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken)
        { Capture(customerId, 0, requestId, quoteId, cancellationToken); return Task.FromResult(Detail); }
        public Task<QuoteListResult> ListForBakeryAsync(long actorUserId, long bakeryId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken)
        { Capture(actorUserId, bakeryId, requestId, 0, cancellationToken); Filter = (status, page, pageSize); return Task.FromResult(List); }
        public Task<QuoteListResult> ListForCustomerAsync(long customerId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken)
            => ListForBakeryAsync(customerId, 0, requestId, status, page, pageSize, cancellationToken);
    }
}
