namespace DeCakes.Api.Authentication;

/// <summary>
/// Endpoint không cần biết người dùng (hiện chỉ health): cookie đăng nhập bị bỏ qua, request được coi là ẩn danh,
/// không truy vấn database để kiểm tra user và không xóa cookie. Không dùng cho endpoint cần xác thực.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SkipSessionValidationAttribute : Attribute;
