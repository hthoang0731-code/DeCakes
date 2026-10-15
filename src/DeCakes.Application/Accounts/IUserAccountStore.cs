namespace DeCakes.Application.Accounts;

/// <summary>Truy cập bảng users. Triển khai ở Infrastructure.</summary>
public interface IUserAccountStore
{
    /// <summary>Tìm theo email đã chuẩn hóa. Trả về cả password_hash, chỉ dùng cho đăng nhập.</summary>
    Task<UserCredentials?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    Task<UserAccount?> FindByIdAsync(long id, CancellationToken cancellationToken);

    /// <summary>Tạo user customer, active, is_demo = 0. Ném <see cref="DuplicateEmailException"/> khi trùng email.</summary>
    Task<long> CreateCustomerAsync(NewCustomer customer, CancellationToken cancellationToken);

    /// <summary>
    /// Thay hash chỉ khi hash hiện tại vẫn là <paramref name="expectedCurrentHash"/>.
    /// Trả về false nếu hash đã bị thay đổi ở nơi khác (không ghi đè).
    /// </summary>
    Task<bool> ReplacePasswordHashAsync(long id, string expectedCurrentHash, string newHash, CancellationToken cancellationToken);
}

public sealed record UserAccount(long Id, string Email, string FullName, string? Phone, string Role, string Status);

public sealed record UserCredentials(UserAccount Account, string? PasswordHash);

public sealed record NewCustomer(string Email, string PasswordHash, string FullName, string? Phone);

public sealed class DuplicateEmailException : Exception
{
    public DuplicateEmailException() : base("Email đã được sử dụng.") { }

    public DuplicateEmailException(Exception innerException) : base("Email đã được sử dụng.", innerException) { }
}
