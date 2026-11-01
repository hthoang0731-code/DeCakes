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

    Task<SaveQuoteResult> CreateAsync(
        long actorUserId, long bakeryId, long requestId, QuoteInput input, CancellationToken cancellationToken);

    Task<SaveQuoteResult> ReviseAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, QuoteInput input, CancellationToken cancellationToken);

    Task<SaveQuoteResult> WithdrawAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken);
}

public abstract record ListQuotesResult
{
    public sealed record Ok(QuoteListResult Result) : ListQuotesResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ListQuotesResult;
}

public abstract record SaveQuoteResult
{
    /// <summary>Phiên bản vừa tạo, phiên bản mới sau khi sửa, hoặc phiên bản vừa rút; đọc lại sau CALL.</summary>
    public sealed record Saved(QuoteDto Quote) : SaveQuoteResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : SaveQuoteResult;

    public sealed record Rejected(QuoteWriteOutcome Reason) : SaveQuoteResult;
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

    public Task<SaveQuoteResult> CreateAsync(
        long actorUserId, long bakeryId, long requestId, QuoteInput input, CancellationToken cancellationToken) =>
        SaveAsync(actorUserId, bakeryId, requestId, input, values =>
            store.CreateAsync(actorUserId, bakeryId, requestId, values, cancellationToken), cancellationToken);

    public Task<SaveQuoteResult> ReviseAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, QuoteInput input, CancellationToken cancellationToken) =>
        SaveAsync(actorUserId, bakeryId, requestId, input, values =>
            store.ReviseAsync(actorUserId, bakeryId, requestId, quoteId, values, cancellationToken), cancellationToken);

    public async Task<SaveQuoteResult> WithdrawAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken) =>
        await ReadBackAsync(actorUserId, bakeryId, requestId,
            await store.WithdrawAsync(actorUserId, bakeryId, requestId, quoteId, cancellationToken), cancellationToken);

    private async Task<SaveQuoteResult> SaveAsync(
        long actorUserId, long bakeryId, long requestId, QuoteInput input, Func<QuoteValues, Task<QuoteWriteResult>> write,
        CancellationToken cancellationToken)
    {
        var (values, errors) = QuoteValidator.Validate(input, timeProvider.GetUtcNow());
        if (values is null) return new SaveQuoteResult.ValidationFailed(errors);

        return await ReadBackAsync(actorUserId, bakeryId, requestId, await write(values), cancellationToken);
    }

    // Procedure không trả result set: đọc lại phiên bản trong cùng phạm vi tiệm/yêu cầu của actor.
    private async Task<SaveQuoteResult> ReadBackAsync(
        long actorUserId, long bakeryId, long requestId, QuoteWriteResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome != QuoteWriteOutcome.Saved) return new SaveQuoteResult.Rejected(result.Outcome);

        var id = result.QuoteId ?? throw new InvalidOperationException("Store không trả id báo giá đã ghi.");
        var read = await GetForBakeryAsync(actorUserId, bakeryId, requestId, id, cancellationToken);
        return read is { Outcome: QuoteReadOutcome.Found, Quote: { } quote }
            ? new SaveQuoteResult.Saved(quote)
            : throw new InvalidOperationException("Không đọc lại được báo giá vừa ghi.");
    }

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
