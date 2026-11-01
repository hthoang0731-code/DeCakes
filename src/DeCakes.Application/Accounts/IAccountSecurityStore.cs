namespace DeCakes.Application.Accounts;

/// <summary>
/// Token tài khoản và thay đổi mật khẩu. Mỗi thao tác tiêu thụ token hoặc đổi hash là một transaction: kiểm tra token
/// dưới khóa dòng, đổi users, đánh dấu token, nên hai lần dùng đồng thời chỉ một lần thành công. Thứ tự khóa:
/// user_tokens rồi users. Thời điểm <c>now</c> do service truyền vào (UTC).
/// </summary>
public interface IAccountSecurityStore
{
    /// <summary>User kèm hash theo id; null nếu không có.</summary>
    Task<UserCredentials?> FindCredentialsByIdAsync(long userId, CancellationToken cancellationToken);

    /// <summary>Số token cùng mục đích đã cấp cho user từ <paramref name="since"/> và lần cấp gần nhất.</summary>
    Task<TokenIssueStats> GetIssueStatsAsync(long userId, string purpose, DateTime since, CancellationToken cancellationToken);

    /// <summary>Thu hồi token chưa dùng cùng mục đích của user rồi thêm token mới, trong một transaction.</summary>
    Task IssueTokenAsync(NewAccountToken token, CancellationToken cancellationToken);

    /// <summary>
    /// Đánh dấu email đã xác minh nếu token đúng mục đích, chưa dùng/thu hồi, chưa hết hạn, email của user vẫn là
    /// email lúc cấp và user active. False cho mọi trường hợp còn lại (không phân biệt lý do).
    /// </summary>
    Task<bool> ConfirmEmailAsync(byte[] tokenHash, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Đặt hash mới, tăng session_version, tiêu thụ token và thu hồi các token đặt lại khác của user, nguyên tử.
    /// Không mở lại user disabled và không áp dụng cho tài khoản demo. False nếu token/user không hợp lệ.
    /// </summary>
    Task<bool> ResetPasswordAsync(byte[] tokenHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken);

    /// <summary>
    /// Đổi hash khi user active và hash hiện tại vẫn là <paramref name="expectedCurrentHash"/>; tăng session_version
    /// và thu hồi token đặt lại còn mở. Trả session_version mới, hoặc null nếu không đổi được.
    /// </summary>
    Task<long?> ChangePasswordAsync(
        long userId, string expectedCurrentHash, string newPasswordHash, DateTime now, CancellationToken cancellationToken);
}

public sealed record NewAccountToken(
    long UserId, string Purpose, byte[] TokenHash, string Email, DateTime CreatedAt, DateTime ExpiresAt);

public sealed record TokenIssueStats(int Count, DateTime? LastIssuedAt);
