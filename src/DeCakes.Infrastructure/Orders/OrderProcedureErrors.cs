using DeCakes.Application.Orders;

namespace DeCakes.Infrastructure.Orders;

/// <summary>
/// Allowlist mã SIGNAL (SQLSTATE 45000, MESSAGE_TEXT) riêng cho từng procedure của Orders, gồm các guard mà procedure đó gọi.
/// Mã ngoài danh sách của đúng procedure (kể cả mã của procedure khác) không được ánh xạ và lan ra thành lỗi 500 đã che.
/// Nguồn có hiệu lực: migrations/V006__order_decision_time_rules.sql (sp_accept_quote, sp_bakery_decide_order),
/// V005 (sp_guard_bakery_entitled) và install/02 (các guard khác).
/// </summary>
internal static class OrderProcedureErrors
{
    public const string SqlState = "45000";

    // sp_accept_quote + sp_guard_active_customer, sp_guard_bakery_active, sp_guard_quote_design, sp_guard_bakery_entitled.
    public static readonly IReadOnlyDictionary<string, OrderWriteOutcome> Accept = new Dictionary<string, OrderWriteOutcome>(StringComparer.Ordinal)
    {
        ["INVALID_ARGUMENT"] = OrderWriteOutcome.InvalidArgument,
        ["QUOTE_NOT_FOUND"] = OrderWriteOutcome.QuoteNotFound,
        // Khách mất quyền giữa lần kiểm tra trước CALL và lúc procedure giữ khóa: như yêu cầu không thuộc khách.
        ["USER_NOT_FOUND"] = OrderWriteOutcome.RequestNotFound,
        ["USER_NOT_ACTIVE"] = OrderWriteOutcome.RequestNotFound,
        ["USER_NOT_CUSTOMER"] = OrderWriteOutcome.RequestNotFound,
        ["NOT_REQUEST_OWNER"] = OrderWriteOutcome.RequestNotFound,
        ["REQUEST_NOT_OPEN"] = OrderWriteOutcome.RequestNotOpen,
        ["QUOTE_NOT_AVAILABLE"] = OrderWriteOutcome.QuoteNotAvailable,
        ["BAKERY_NOT_FOUND"] = OrderWriteOutcome.BakeryNotActive,
        ["BAKERY_NOT_ACTIVE"] = OrderWriteOutcome.BakeryNotActive,
        ["DESIGN_ASSET_NOT_FOUND"] = OrderWriteOutcome.DesignAssetInvalid,
        ["DESIGN_ASSET_WRONG_REQUEST"] = OrderWriteOutcome.DesignAssetInvalid,
        ["DESIGN_ASSET_WRONG_BAKERY"] = OrderWriteOutcome.DesignAssetInvalid,
        ["BAKERY_SUBSCRIPTION_INACTIVE"] = OrderWriteOutcome.BakerySubscriptionInactive,
        ["QUOTE_EXPIRED"] = OrderWriteOutcome.QuoteExpired,
        ["QUOTE_PICKUP_PASSED"] = OrderWriteOutcome.QuotePickupPassed,
    };

    // sp_bakery_decide_order + sp_guard_active_bakery_member, sp_guard_bakery_active (chỉ khi confirm). Không kiểm tra gói.
    public static readonly IReadOnlyDictionary<string, OrderWriteOutcome> Decide = new Dictionary<string, OrderWriteOutcome>(StringComparer.Ordinal)
    {
        ["INVALID_ARGUMENT"] = OrderWriteOutcome.InvalidArgument,
        ["INVALID_DECISION"] = OrderWriteOutcome.InvalidArgument,
        ["ORDER_REJECTION_REASON_REQUIRED"] = OrderWriteOutcome.RejectionReasonRequired,
        ["ORDER_NOT_FOUND"] = OrderWriteOutcome.OrderNotFound,
        ["USER_NOT_FOUND"] = OrderWriteOutcome.BakeryNotFound,
        ["USER_NOT_ACTIVE"] = OrderWriteOutcome.BakeryNotFound,
        ["NOT_BAKERY_MEMBER"] = OrderWriteOutcome.BakeryNotFound,
        ["ORDER_NOT_PENDING"] = OrderWriteOutcome.OrderNotPending,
        ["BAKERY_NOT_FOUND"] = OrderWriteOutcome.BakeryNotFound,
        ["BAKERY_NOT_ACTIVE"] = OrderWriteOutcome.BakeryNotActive,
        ["ORDER_PICKUP_PASSED"] = OrderWriteOutcome.OrderPickupPassed,
    };

    public static bool TryMap(
        IReadOnlyDictionary<string, OrderWriteOutcome> allowlist, string? sqlState, string? message, out OrderWriteOutcome outcome)
    {
        outcome = default;
        return sqlState == SqlState && message is not null && allowlist.TryGetValue(message, out outcome);
    }
}
