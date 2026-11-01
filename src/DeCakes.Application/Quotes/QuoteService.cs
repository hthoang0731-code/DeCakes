using DeCakes.Application.Common;
using DeCakes.Application.DesignRequests;
using DeCakes.Domain.Quotes;

namespace DeCakes.Application.Quotes;

public interface IQuoteService
{
    Task<QuoteReadResult> GetForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken);

    Task<ListQuotesResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, string? status, int? page, int? pageSize,
        CancellationToken cancellationToken);

    Task<QuoteReadResult> GetForCustomerAsync(
        long customerId, long requestId, long quoteId, CancellationToken cancellationToken);

    Task<ListQuotesResult> ListForCustomerAsync(
        long customerId, long requestId, string? status, int? page, int? pageSize, CancellationToken cancellationToken);
}

public abstract record ListQuotesResult
{
    public sealed record Ok(QuoteListResult Result) : ListQuotesResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ListQuotesResult;
}

public sealed class QuoteService(IQuoteStore store, TimeProvider timeProvider) : IQuoteService
{
    public async Task<QuoteReadResult> GetForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken) =>
        WithExpiry(await store.FindForBakeryAsync(actorUserId, bakeryId, requestId, quoteId, cancellationToken));

    public async Task<QuoteReadResult> GetForCustomerAsync(
        long customerId, long requestId, long quoteId, CancellationToken cancellationToken) =>
        WithExpiry(await store.FindForCustomerAsync(customerId, requestId, quoteId, cancellationToken));

    public Task<ListQuotesResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, string? status, int? page, int? pageSize,
        CancellationToken cancellationToken) =>
        ListAsync(status, page, pageSize, (filter, number, size) =>
            store.ListForBakeryAsync(actorUserId, bakeryId, requestId, filter, number, size, cancellationToken));

    public Task<ListQuotesResult> ListForCustomerAsync(
        long customerId, long requestId, string? status, int? page, int? pageSize, CancellationToken cancellationToken) =>
        ListAsync(status, page, pageSize, (filter, number, size) =>
            store.ListForCustomerAsync(customerId, requestId, filter, number, size, cancellationToken));

    private async Task<ListQuotesResult> ListAsync(
        string? status, int? page, int? pageSize, Func<string?, int, int, Task<QuoteListResult>> query)
    {
        var errors = new Dictionary<string, string[]>();
        var filter = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (filter is not null && !QuoteStatuses.IsKnown(filter))
        {
            errors["status"] = ["Trạng thái phải là submitted, superseded, withdrawn, accepted hoặc not_selected."];
        }

        var (number, size) = Paging.Normalize(page, pageSize, errors);
        if (errors.Count > 0) return new ListQuotesResult.ValidationFailed(errors);

        var result = await query(filter, number, size);
        if (result.Page is not { } data) return new ListQuotesResult.Ok(result);

        // Một thời điểm đánh giá cho toàn bộ trang, kể cả khi đang đi qua ranh giới hết hạn.
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var items = data.Items.Select(quote => quote with { IsExpired = quote.ValidUntil <= now }).ToList();
        return new ListQuotesResult.Ok(result with
        {
            Page = new PagedResult<QuoteDto>(items, data.Page, data.PageSize, data.TotalCount),
        });
    }

    private QuoteReadResult WithExpiry(QuoteReadResult result) => result.Quote is { } quote
        ? result with { Quote = quote with { IsExpired = quote.ValidUntil <= timeProvider.GetUtcNow().UtcDateTime } }
        : result;
}
