using DeCakes.Application.Quotes;

namespace DeCakes.Infrastructure.Quotes;

/// <summary>
/// Allowlist mã SIGNAL (SQLSTATE 45000, MESSAGE_TEXT) riêng cho từng procedure ghi báo giá, gồm các guard mà procedure đó gọi.
/// Mã ngoài danh sách của đúng procedure (kể cả mã của procedure khác) không được ánh xạ và lan ra thành lỗi 500 đã che.
/// Nguồn: database/install/02_procedures.sql (sp_withdraw_quote, các guard) và migrations/V005__subscription_payments.sql
/// (sp_create_quote, sp_revise_quote, sp_guard_bakery_entitled).
/// </summary>
internal static class QuoteProcedureErrors
{
    public const string SqlState = "45000";

    // sp_guard_active_bakery_member: thành viên mất quyền giữa lần kiểm tra trước CALL và lúc procedure giữ khóa.
    private static readonly Dictionary<string, QuoteWriteOutcome> Member = new(StringComparer.Ordinal)
    {
        ["INVALID_ARGUMENT"] = QuoteWriteOutcome.InvalidArgument,
        ["USER_NOT_FOUND"] = QuoteWriteOutcome.BakeryNotFound,
        ["USER_NOT_ACTIVE"] = QuoteWriteOutcome.BakeryNotFound,
        ["NOT_BAKERY_MEMBER"] = QuoteWriteOutcome.BakeryNotFound,
    };

    // sp_guard_bakery_active, sp_guard_quote_design, sp_guard_bakery_entitled, sp_guard_quote_content.
    private static readonly Dictionary<string, QuoteWriteOutcome> Content = new(StringComparer.Ordinal)
    {
        ["BAKERY_NOT_FOUND"] = QuoteWriteOutcome.BakeryNotFound,
        ["BAKERY_NOT_ACTIVE"] = QuoteWriteOutcome.BakeryNotActive,
        ["DESIGN_ASSET_NOT_FOUND"] = QuoteWriteOutcome.DesignAssetInvalid,
        ["DESIGN_ASSET_WRONG_REQUEST"] = QuoteWriteOutcome.DesignAssetInvalid,
        ["DESIGN_ASSET_WRONG_BAKERY"] = QuoteWriteOutcome.DesignAssetInvalid,
        ["BAKERY_SUBSCRIPTION_INACTIVE"] = QuoteWriteOutcome.BakerySubscriptionInactive,
        ["QUOTE_INVALID_PRICE"] = QuoteWriteOutcome.InvalidPrice,
        ["QUOTE_INVALID_TIMES"] = QuoteWriteOutcome.InvalidTimes,
    };

    public static readonly IReadOnlyDictionary<string, QuoteWriteOutcome> Create = Merge(Member, Content, new()
    {
        ["REQUEST_NOT_FOUND"] = QuoteWriteOutcome.RequestNotFound,
        ["REQUEST_NOT_SENT_TO_BAKERY"] = QuoteWriteOutcome.RequestNotFound,
        ["REQUEST_NOT_OPEN"] = QuoteWriteOutcome.RequestNotOpen,
        ["REQUEST_DECLINED_BY_BAKERY"] = QuoteWriteOutcome.RequestDeclined,
        ["QUOTE_ALREADY_EXISTS"] = QuoteWriteOutcome.QuoteAlreadyExists,
    });

    public static readonly IReadOnlyDictionary<string, QuoteWriteOutcome> Revise = Merge(Member, Content, new()
    {
        ["QUOTE_NOT_FOUND"] = QuoteWriteOutcome.QuoteNotFound,
        ["QUOTE_ALREADY_ACCEPTED"] = QuoteWriteOutcome.QuoteAlreadyAccepted,
        ["REQUEST_NOT_OPEN"] = QuoteWriteOutcome.RequestNotOpen,
        ["QUOTE_NOT_CURRENT"] = QuoteWriteOutcome.QuoteNotCurrent,
    });

    // Rút không gọi guard tiệm/gói/nội dung: vẫn rút được khi tiệm suspended hoặc hết hạn gói.
    public static readonly IReadOnlyDictionary<string, QuoteWriteOutcome> Withdraw = Merge(Member, new(), new()
    {
        ["QUOTE_NOT_FOUND"] = QuoteWriteOutcome.QuoteNotFound,
        ["QUOTE_ALREADY_ACCEPTED"] = QuoteWriteOutcome.QuoteAlreadyAccepted,
        ["QUOTE_NOT_CURRENT"] = QuoteWriteOutcome.QuoteNotCurrent,
    });

    public static bool TryMap(
        IReadOnlyDictionary<string, QuoteWriteOutcome> allowlist, string? sqlState, string? message, out QuoteWriteOutcome outcome)
    {
        outcome = default;
        return sqlState == SqlState && message is not null && allowlist.TryGetValue(message, out outcome);
    }

    private static Dictionary<string, QuoteWriteOutcome> Merge(params Dictionary<string, QuoteWriteOutcome>[] parts)
    {
        var merged = new Dictionary<string, QuoteWriteOutcome>(StringComparer.Ordinal);
        foreach (var (code, outcome) in parts.SelectMany(part => part))
        {
            merged.Add(code, outcome);
        }

        return merged;
    }
}
