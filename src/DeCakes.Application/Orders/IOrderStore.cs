using DeCakes.Application.DesignRequests;

namespace DeCakes.Application.Orders;

/// <summary>Mỗi truy vấn kiểm tra lại user active và quyền sở hữu (khách) hoặc membership của đúng tiệm trên route trong SQL.</summary>
public interface IOrderStore
{
    Task<OrderListResult> ListForCustomerAsync(
        long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<OrderReadResult> FindForCustomerAsync(long customerId, long orderId, CancellationToken cancellationToken);

    Task<OrderReadResult> FindForCustomerRequestAsync(long customerId, long requestId, CancellationToken cancellationToken);

    Task<OrderListResult> ListForBakeryAsync(
        long actorUserId, long bakeryId, string? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<OrderReadResult> FindForBakeryAsync(long actorUserId, long bakeryId, long orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Khách chọn báo giá qua sp_accept_quote (procedure tự quản lý transaction, không retry). Trước CALL kiểm tra database đã
    /// có migration V006 và báo giá thuộc đúng yêu cầu của khách trên route. Trả về id đơn từ OUT p_order_id.
    /// </summary>
    Task<OrderWriteResult> AcceptAsync(long customerId, long requestId, long quoteId, CancellationToken cancellationToken);

    /// <summary>
    /// Tiệm xác nhận/từ chối qua sp_bakery_decide_order. Trước CALL kiểm tra V006, actor là thành viên active của tiệm trên route
    /// và đơn thuộc đúng tiệm đó. <paramref name="reason"/> đã được service kiểm tra (null khi confirm).
    /// </summary>
    Task<OrderWriteResult> DecideAsync(
        long actorUserId, long bakeryId, long orderId, string decision, string? reason, CancellationToken cancellationToken);
}

public enum OrderReadOutcome
{
    Found,
    BakeryNotFound,
    RequestNotFound,
    OrderNotFound,
}

public sealed record OrderReadResult(OrderReadOutcome Outcome, OrderDto? Order = null);

public sealed record OrderListResult(OrderReadOutcome Outcome, PagedResult<OrderDto>? Page = null);

/// <summary>Kết quả ghi, từ kiểm tra trước CALL hoặc mã SIGNAL trong allowlist của từng procedure.</summary>
public enum OrderWriteOutcome
{
    Saved,
    WorkflowNotReady,            // database chưa có migration V006: không gọi procedure
    RequestNotFound,             // không thuộc khách; NOT_REQUEST_OWNER, USER_NOT_FOUND, USER_NOT_ACTIVE, USER_NOT_CUSTOMER
    QuoteNotFound,               // không thuộc đúng yêu cầu; QUOTE_NOT_FOUND
    BakeryNotFound,              // không phải thành viên active; NOT_BAKERY_MEMBER, USER_NOT_*, BAKERY_NOT_FOUND
    OrderNotFound,               // không thuộc đúng tiệm; ORDER_NOT_FOUND
    InvalidArgument,             // INVALID_ARGUMENT, INVALID_DECISION
    RejectionReasonRequired,     // ORDER_REJECTION_REASON_REQUIRED (V006)
    RequestNotOpen,              // REQUEST_NOT_OPEN (đã có đơn hoặc đã hủy)
    QuoteNotAvailable,           // QUOTE_NOT_AVAILABLE (superseded, withdrawn, accepted, not_selected)
    QuoteExpired,                // QUOTE_EXPIRED (giờ database sau mọi lần đọc có khóa)
    QuotePickupPassed,           // QUOTE_PICKUP_PASSED (V006)
    DesignAssetInvalid,          // DESIGN_ASSET_NOT_FOUND, DESIGN_ASSET_WRONG_REQUEST, DESIGN_ASSET_WRONG_BAKERY
    BakeryNotActive,             // BAKERY_NOT_ACTIVE (accept cũng gồm BAKERY_NOT_FOUND)
    BakerySubscriptionInactive,  // BAKERY_SUBSCRIPTION_INACTIVE (V005, chỉ accept)
    OrderNotPending,             // ORDER_NOT_PENDING
    OrderPickupPassed,           // ORDER_PICKUP_PASSED (V006, chỉ confirm)
}

public sealed record OrderWriteResult(OrderWriteOutcome Outcome, long? OrderId = null);
