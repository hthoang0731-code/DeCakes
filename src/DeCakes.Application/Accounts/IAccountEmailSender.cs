namespace DeCakes.Application.Accounts;

/// <summary>
/// Gửi email tài khoản. Triển khai ở Infrastructure tự dựng link HTTPS từ origin cấu hình (không lấy từ Host của
/// request). Không ghi token vào log. Ném <see cref="EmailDeliveryException"/> khi chưa cấu hình hoặc gửi thất bại.
/// </summary>
public interface IAccountEmailSender
{
    Task SendEmailVerificationAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken);

    Task SendPasswordResetAsync(string toEmail, string fullName, string token, DateTime expiresAt, CancellationToken cancellationToken);

    /// <summary>Lời mời làm staff của tiệm; người nhận tự đặt mật khẩu hoặc đăng nhập tài khoản bakery đúng email.</summary>
    Task SendStaffInvitationAsync(string toEmail, string bakeryName, string token, DateTime expiresAt, CancellationToken cancellationToken);
}

/// <summary>Thông điệp không chứa token, địa chỉ email hay khóa API.</summary>
public sealed class EmailDeliveryException(string message, Exception? innerException = null) : Exception(message, innerException);
