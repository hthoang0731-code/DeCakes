using DeCakes.Application.DesignRequests;

namespace DeCakes.Infrastructure.DesignRequests;

/// <summary>
/// Allowlist mã SIGNAL (SQLSTATE 45000, MESSAGE_TEXT) mà sp_send_request_to_bakery và các guard nó gọi
/// (sp_guard_active_customer, sp_guard_bakery_active) có thể trả. Mã ngoài danh sách không được ánh xạ.
/// Nguồn: database/install/02_procedures.sql và migrations/V005__subscription_payments.sql.
/// </summary>
internal static class SendRequestProcedureErrors
{
    public const string SqlState = "45000";

    private static readonly Dictionary<string, SendToBakeryOutcome> Map = new(StringComparer.Ordinal)
    {
        ["INVALID_ARGUMENT"] = SendToBakeryOutcome.InvalidArgument,
        ["REQUEST_NOT_FOUND"] = SendToBakeryOutcome.RequestNotFound,
        ["NOT_REQUEST_OWNER"] = SendToBakeryOutcome.RequestNotFound,
        ["BAKERY_NOT_FOUND"] = SendToBakeryOutcome.BakeryNotFound,
        ["REQUEST_ALREADY_SENT"] = SendToBakeryOutcome.AlreadySent,
        ["REQUEST_NOT_OPEN"] = SendToBakeryOutcome.RequestNotOpen,
        ["REQUEST_PICKUP_IN_PAST"] = SendToBakeryOutcome.PickupInPast,
        ["BAKERY_NOT_ACTIVE"] = SendToBakeryOutcome.BakeryNotActive,
        ["USER_NOT_FOUND"] = SendToBakeryOutcome.AccountNotAllowed,
        ["USER_NOT_ACTIVE"] = SendToBakeryOutcome.AccountNotAllowed,
        ["USER_NOT_CUSTOMER"] = SendToBakeryOutcome.AccountNotAllowed,
        // migrations/V005: sp_guard_bakery_entitled (tiệm không có kỳ sử dụng còn hạn).
        ["BAKERY_SUBSCRIPTION_INACTIVE"] = SendToBakeryOutcome.BakerySubscriptionInactive,
    };

    public static IReadOnlyCollection<string> KnownCodes => Map.Keys;

    public static bool TryMap(string? sqlState, string? message, out SendToBakeryOutcome outcome)
    {
        outcome = default;
        return sqlState == SqlState && message is not null && Map.TryGetValue(message, out outcome);
    }
}
