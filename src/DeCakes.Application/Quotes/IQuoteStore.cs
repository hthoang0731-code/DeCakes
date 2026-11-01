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

    /// <summary>
    /// Ghi qua sp_create_quote, sp_revise_quote, sp_withdraw_quote (procedure tự quản lý transaction, không retry).
    /// Trước CALL kiểm tra actor là thành viên active của đúng tiệm trên route, yêu cầu đã gửi đến tiệm đó và báo giá
    /// thuộc đúng cặp tiệm/yêu cầu. Trả về id phiên bản (OUT, hoặc id đã rút) hay mã lỗi đã nhận diện; lỗi khác được ném ra.
    /// </summary>
    Task<QuoteWriteResult> CreateAsync(
        long actorUserId, long bakeryId, long requestId, QuoteValues values, CancellationToken cancellationToken);

    Task<QuoteWriteResult> ReviseAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, QuoteValues values, CancellationToken cancellationToken);

    Task<QuoteWriteResult> WithdrawAsync(
        long actorUserId, long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken);
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

/// <summary>Kết quả ghi, từ kiểm tra phạm vi trước CALL hoặc mã SIGNAL trong allowlist của từng procedure.</summary>
public enum QuoteWriteOutcome
{
    Saved,
    BakeryNotFound,             // không phải thành viên active; NOT_BAKERY_MEMBER, USER_NOT_FOUND, USER_NOT_ACTIVE, BAKERY_NOT_FOUND
    RequestNotFound,            // chưa gửi đến tiệm; REQUEST_NOT_FOUND, REQUEST_NOT_SENT_TO_BAKERY
    QuoteNotFound,              // không thuộc đúng tiệm/yêu cầu; QUOTE_NOT_FOUND
    InvalidArgument,            // INVALID_ARGUMENT
    InvalidPrice,               // QUOTE_INVALID_PRICE
    InvalidTimes,               // QUOTE_INVALID_TIMES (so với giờ database sau khi giữ khóa)
    DesignAssetInvalid,         // DESIGN_ASSET_NOT_FOUND, DESIGN_ASSET_WRONG_REQUEST, DESIGN_ASSET_WRONG_BAKERY
    RequestNotOpen,             // REQUEST_NOT_OPEN
    RequestDeclined,            // REQUEST_DECLINED_BY_BAKERY
    QuoteAlreadyExists,         // QUOTE_ALREADY_EXISTS
    QuoteAlreadyAccepted,       // QUOTE_ALREADY_ACCEPTED
    QuoteNotCurrent,            // QUOTE_NOT_CURRENT
    BakeryNotActive,            // BAKERY_NOT_ACTIVE
    BakerySubscriptionInactive, // BAKERY_SUBSCRIPTION_INACTIVE (migrations/V005)
}

public sealed record QuoteWriteResult(QuoteWriteOutcome Outcome, long? QuoteId = null);
