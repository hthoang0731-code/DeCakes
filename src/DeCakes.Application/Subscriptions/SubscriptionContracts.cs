using DeCakes.Application.BakeryOnboarding;

namespace DeCakes.Application.Subscriptions;

public sealed record CheckoutRequest(string? BillingCycle);

public sealed record SubscriptionPeriodDto(string Kind, DateTime StartsAt, DateTime EndsAt);

/// <summary>
/// Quyền sử dụng của tiệm tính tại thời điểm đọc từ các kỳ (không dựa vào cột trạng thái do job cập nhật).
/// Giá null = gói chưa định giá, chưa mua được.
/// </summary>
public sealed record SubscriptionStatusDto(
    long BakeryId,
    SelectedPlanDto? Plan,
    decimal? MonthlyPrice,
    decimal? YearlyPrice,
    bool AccessActive,
    DateTime? AccessEndsAt,
    IReadOnlyList<SubscriptionPeriodDto> Periods);

public sealed record SubscriptionPaymentDto(
    long Id,
    long PlanId,
    string BillingCycle,
    decimal Amount,
    string Currency,
    string Status,
    string? CheckoutUrl,
    DateTime CreatedAt,
    DateTime? PaidAt);

/// <summary>Dữ liệu gói/kỳ của tiệm cho thành viên active; null khi không phải thành viên.</summary>
public sealed record SubscriptionStatusRecord(
    long BakeryId,
    SelectedPlanDto? Plan,
    decimal? MonthlyPrice,
    decimal? YearlyPrice,
    IReadOnlyList<SubscriptionPeriodDto> Periods);

public sealed record NewSubscriptionPayment(
    long OwnerUserId, long BakeryId, string BillingCycle, string Provider, long OrderCode, DateTime Now);

public enum CheckoutOutcome
{
    Created,
    BakeryNotFound,
    OwnerRequired,
    /// <summary>Chỉ tiệm đã được duyệt (active) mới mua/gia hạn; thời hạn khi chưa duyệt/đình chỉ chưa chốt.</summary>
    ProfileNotActive,
    PlanNotSelected,
    PlanNotAvailable,
    /// <summary>Gói chưa định giá cho chu kỳ này: không mở checkout, không dùng giá 0.</summary>
    PlanNotPriced,
    /// <summary>Cổng thanh toán chưa bật/chưa đủ cấu hình merchant: không gọi nhà cung cấp, không tạo giao dịch.</summary>
    PaymentNotConfigured,
    ProviderError,
}

public sealed record CreatePaymentResult(CheckoutOutcome Status, long PaymentId = 0, decimal Amount = 0, string? Currency = null);

public enum PaymentApplyOutcome
{
    /// <summary>Đã xác minh, khớp, tạo đúng một kỳ trả phí.</summary>
    Activated,
    /// <summary>Đã xử lý trước đó (webhook lặp/đến trễ): không đổi gì.</summary>
    AlreadyProcessed,
    UnknownPayment,
    /// <summary>Chữ ký hợp lệ nhưng số tiền/tiền tệ/mã link không khớp: đánh dấu mismatch, không cấp quyền.</summary>
    Mismatch,
}

/// <summary>Kết quả thanh toán đã được xác minh chữ ký bởi cổng thanh toán.</summary>
public sealed record VerifiedPaymentNotice(
    string Provider, long OrderCode, decimal Amount, string? Currency, string? ProviderPaymentId, string? Reference);

/// <summary>Giao dịch phí sử dụng DeCakes (không phải tiền bánh). Mọi thao tác theo tiệm tự kiểm tra thành viên/owner.</summary>
public interface ISubscriptionStore
{
    Task<SubscriptionStatusRecord?> FindStatusAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    /// <summary>
    /// Owner, hồ sơ active, gói đã chọn đang mở và có giá cho chu kỳ: ghi giao dịch 'created' với số tiền/gói chốt lúc
    /// này. Ném <see cref="DuplicateOrderCodeException"/> khi trùng mã đơn.
    /// </summary>
    Task<CreatePaymentResult> CreatePaymentAsync(NewSubscriptionPayment payment, CancellationToken cancellationToken);

    Task MarkPaymentLinkAsync(long paymentId, string providerPaymentId, string checkoutUrl, DateTime now, CancellationToken cancellationToken);

    Task MarkPaymentFailedAsync(long paymentId, string reason, DateTime now, CancellationToken cancellationToken);

    Task<(OwnerActionOutcome Outcome, SubscriptionPaymentDto? Payment)> FindPaymentAsync(
        long ownerUserId, long bakeryId, long paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Khóa giao dịch theo mã đơn, đối chiếu số tiền/tiền tệ/mã link, rồi (khóa dòng gói của tiệm để tuần tự hóa theo
    /// tiệm) tạo một kỳ trả phí nối tiếp ngày hết hạn hiện tại và đánh dấu paid, trong một transaction.
    /// </summary>
    Task<PaymentApplyOutcome> ApplyVerifiedPaymentAsync(VerifiedPaymentNotice notice, DateTime now, CancellationToken cancellationToken);
}

public sealed class DuplicateOrderCodeException(Exception innerException) : Exception("Trùng mã đơn thanh toán.", innerException);

/// <summary>
/// Cổng thanh toán. Triển khai đọc cấu hình merchant ở Infrastructure; khi chưa bật/chưa đủ cấu hình,
/// <see cref="IsEnabled"/> là false và không có lời gọi mạng nào.
/// </summary>
public interface ISubscriptionPaymentGateway
{
    string Provider { get; }

    bool IsEnabled { get; }

    /// <summary>Ném <see cref="PaymentGatewayException"/> khi nhà cung cấp lỗi hoặc phản hồi không khớp yêu cầu.</summary>
    Task<PaymentLink> CreatePaymentLinkAsync(PaymentLinkRequest request, CancellationToken cancellationToken);

    /// <summary>Xác minh chữ ký webhook trên nội dung thô; không tin dữ liệu chưa xác minh.</summary>
    WebhookVerification VerifyWebhook(string body);
}

public sealed record PaymentLinkRequest(long OrderCode, long Amount, string Description, DateTime ExpiresAt);

public sealed record PaymentLink(string ProviderPaymentId, string CheckoutUrl);

public enum WebhookVerificationStatus
{
    NotConfigured,
    InvalidSignature,
    /// <summary>Chữ ký hợp lệ nhưng không phải thanh toán thành công (hoặc thiếu dữ liệu): không cấp quyền.</summary>
    NotPaid,
    Paid,
}

public sealed record WebhookVerification(WebhookVerificationStatus Status, VerifiedPaymentNotice? Notice = null);

/// <summary>Thông điệp không chứa khóa, chữ ký hay nội dung phản hồi của nhà cung cấp.</summary>
public sealed class PaymentGatewayException(string message, Exception? innerException = null) : Exception(message, innerException);
