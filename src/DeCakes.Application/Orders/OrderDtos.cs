namespace DeCakes.Application.Orders;

/// <summary>
/// Đơn và bản chốt, đọc từ orders.agreed_* (không dựng lại từ báo giá, yêu cầu hay ảnh hiện tại). Không gồm customer_id,
/// thông tin liên hệ, người quyết định hay URL ảnh (cơ chế phục vụ ảnh riêng tư chưa chốt); ảnh chỉ có metadata.
/// BakeryName là tên hiện tại của tiệm, không thuộc bản chốt.
/// </summary>
public sealed record OrderDto(
    long Id,
    long RequestId,
    long QuoteId,
    long BakeryId,
    string BakeryName,
    string Status,
    long AgreedQuoteRevisionNo,
    decimal AgreedPrice,
    string Currency,
    DateTime AgreedPickupAt,
    string? AgreedSizeDescription,
    int? AgreedServings,
    string AgreedFlavor,
    string AgreedDecorationDescription,
    string? AgreedInscriptionText,
    string? AgreedNote,
    long? AgreedDesignAssetId,
    string? AgreedDesignAssetKind,
    long? AgreedDesignVersionNo,
    DateTime? BakeryDecidedAt,
    string? RejectionReason,
    DateTime? PickedUpAt,
    DateTime? CancelledAt,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Quyết định của tiệm: decision là "confirm" hoặc "reject". Reason bắt buộc khi reject; khi confirm phải bỏ trống
/// (chuỗi rỗng/khoảng trắng coi như không gửi), lý do có nội dung bị từ chối thay vì âm thầm bỏ qua.
/// </summary>
public sealed record OrderDecisionInput(string? Decision, string? Reason);
