namespace DeCakes.Api.Authentication;

/// <summary>
/// Endpoint server-to-server do nhà cung cấp gọi (không có cookie/CSRF token của trình duyệt). Chỉ bỏ kiểm tra CSRF cho
/// đúng action mang attribute này; action BẮT BUỘC tự xác minh chữ ký trên nội dung trước khi xử lý và xử lý lặp an toàn.
/// Không bỏ kiểm tra phiên (khác <see cref="SkipSessionValidationAttribute"/> của health) và không dùng cho endpoint của
/// trình duyệt.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SignedWebhookAttribute : Attribute;
