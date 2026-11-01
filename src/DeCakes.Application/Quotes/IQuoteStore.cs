using DeCakes.Application.DesignRequests;

namespace DeCakes.Application.Quotes;

/// <summary>Mỗi truy vấn kiểm tra lại chủ yêu cầu hoặc thành viên tiệm trong SQL.</summary>
public interface IQuoteStore
{
    Task<QuoteListResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, string? status, int page, int pageSize,
        CancellationToken cancellationToken);

    Task<QuoteReadResult> FindForBakeryAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken);

    Task<QuoteListResult> ListForCustomerAsync(
        long customerId, long requestId, string? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<QuoteReadResult> FindForCustomerAsync(
        long customerId, long requestId, long quoteId, CancellationToken cancellationToken);
}

public enum QuoteReadOutcome
{
    Found,
    BakeryNotFound,
    RequestNotFound,
    QuoteNotFound,
}

public sealed record QuoteReadResult(QuoteReadOutcome Outcome, QuoteDto? Quote = null);

public sealed record QuoteListResult(QuoteReadOutcome Outcome, PagedResult<QuoteDto>? Page = null);
