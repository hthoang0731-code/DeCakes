namespace DeCakes.Application.Accounts;

// Thuộc tính nullable: mọi kiểm tra đầu vào nằm ở AccountService, không phụ thuộc model binding.
public sealed record RegisterRequest(string? Email, string? Password, string? FullName, string? Phone);

public sealed record LoginRequest(string? Email, string? Password);

public sealed record ConfirmEmailRequest(string? Token);

public sealed record ForgotPasswordRequest(string? Email);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Thông tin người dùng an toàn để trả về client: không có mật khẩu hay hash.</summary>
public sealed record UserDto(long Id, string Email, string FullName, string? Phone, string Role, bool EmailVerified);

/// <summary>
/// User của một phiên đã kiểm tra, kèm <c>SessionVersion</c> hiện tại trong database. Cookie mang giá trị lúc cấp;
/// lệch nhau nghĩa là mật khẩu đã đổi/đặt lại sau khi cookie được cấp. Không trả SessionVersion cho client.
/// </summary>
public sealed record SessionUser(UserDto User, long SessionVersion);
