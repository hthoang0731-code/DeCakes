namespace DeCakes.Application.Accounts;

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>Hash và xác minh mật khẩu. Triển khai ở Infrastructure.</summary>
public interface IPasswordHashService
{
    string Hash(string password);

    /// <summary>
    /// Xác minh mật khẩu. Khi <paramref name="hashedPassword"/> là null hoặc không đúng định dạng,
    /// vẫn thực hiện một lần xác minh trên hash giả có cùng chi phí rồi trả về <see cref="PasswordCheck.Failed"/>.
    /// </summary>
    PasswordCheck Verify(string? hashedPassword, string password);
}
