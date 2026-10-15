namespace DeCakes.Application.Accounts;

// Thuộc tính nullable: mọi kiểm tra đầu vào nằm ở AccountService, không phụ thuộc model binding.
public sealed record RegisterRequest(string? Email, string? Password, string? FullName, string? Phone);

public sealed record LoginRequest(string? Email, string? Password);

/// <summary>Thông tin người dùng an toàn để trả về client: không có mật khẩu hay hash.</summary>
public sealed record UserDto(long Id, string Email, string FullName, string? Phone, string Role);
