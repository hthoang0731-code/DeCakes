using System.Net.Mail;
using System.Text;
using DeCakes.Domain.Accounts;

namespace DeCakes.Application.Accounts;

public interface IAccountService
{
    Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken);

    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    /// <summary>Trả về user nếu còn tồn tại, active và có role hợp lệ; ngược lại null.</summary>
    Task<UserDto?> GetActiveUserAsync(long userId, CancellationToken cancellationToken);

    /// <summary>Như <see cref="GetActiveUserAsync"/>, kèm session_version hiện tại để kiểm tra cookie.</summary>
    Task<SessionUser?> GetSessionAsync(long userId, CancellationToken cancellationToken);
}

public abstract record RegisterResult
{
    public sealed record Created(UserDto User) : RegisterResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : RegisterResult;

    public sealed record EmailTaken : RegisterResult;
}

public abstract record LoginResult
{
    public sealed record Succeeded(UserDto User, long SessionVersion) : LoginResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : LoginResult;

    /// <summary>Một kết quả chung cho: không có user, chưa có/hỏng hash, sai mật khẩu, user không active.</summary>
    public sealed record InvalidCredentials : LoginResult;
}

public sealed class AccountService(IUserAccountStore store, IPasswordHashService passwordHasher) : IAccountService
{
    // Giới hạn theo schema users và quy định mật khẩu.
    public const int EmailMaxLength = 255;
    public const int FullNameMaxLength = 150;
    public const int PhoneMaxLength = 20;
    public const int PasswordMinLength = 12;
    public const int PasswordMaxLength = 128;

    public async Task<RegisterResult> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var account = NormalizeNewAccount(request, errors);
        if (account is null)
        {
            return new RegisterResult.ValidationFailed(errors);
        }

        var hash = passwordHasher.Hash(account.Password);
        try
        {
            var id = await store.CreateCustomerAsync(new NewCustomer(account.Email, hash, account.FullName, account.Phone), cancellationToken);
            return new RegisterResult.Created(
                new UserDto(id, account.Email, account.FullName, account.Phone, SystemRoles.Customer, EmailVerified: false));
        }
        catch (DuplicateEmailException)
        {
            return new RegisterResult.EmailTaken();
        }
    }

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Email)) errors["email"] = ["Email là bắt buộc."];
            if (string.IsNullOrEmpty(request.Password)) errors["password"] = ["Mật khẩu là bắt buộc."];
            return new LoginResult.ValidationFailed(errors);
        }

        var password = request.Password;
        // Chặn đầu vào quá dài trước khi hash (không phụ thuộc tài khoản nên không lộ thông tin).
        if (password.Length > PasswordMaxLength)
        {
            return new LoginResult.InvalidCredentials();
        }

        var email = NormalizeEmail(request.Email)!;
        var credentials = email.Length <= EmailMaxLength
            ? await store.FindByEmailAsync(email, cancellationToken)
            : null;

        // Luôn chạy một lần xác minh, kể cả khi không có user hoặc hash NULL (hash giả trong IPasswordHashService).
        var check = passwordHasher.Verify(credentials?.PasswordHash, password);

        if (credentials is null
            || check == PasswordCheck.Failed
            || credentials.Account.Status != UserStatuses.Active
            || !SystemRoles.IsKnown(credentials.Account.Role))
        {
            return new LoginResult.InvalidCredentials();
        }

        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            var newHash = passwordHasher.Hash(password);
            // Nếu hash đã đổi ở nơi khác, không ghi đè; đăng nhập vẫn hợp lệ.
            await store.ReplacePasswordHashAsync(credentials.Account.Id, credentials.PasswordHash!, newHash, cancellationToken);
        }

        return new LoginResult.Succeeded(ToDto(credentials.Account), credentials.Account.SessionVersion);
    }

    public async Task<UserDto?> GetActiveUserAsync(long userId, CancellationToken cancellationToken) =>
        (await GetSessionAsync(userId, cancellationToken))?.User;

    public async Task<SessionUser?> GetSessionAsync(long userId, CancellationToken cancellationToken)
    {
        var account = await store.FindByIdAsync(userId, cancellationToken);
        return account is not null && account.Status == UserStatuses.Active && SystemRoles.IsKnown(account.Role)
            ? new SessionUser(ToDto(account), account.SessionVersion)
            : null;
    }

    /// <summary>Dữ liệu tài khoản mới đã chuẩn hóa. Mật khẩu giữ nguyên (không trim).</summary>
    public sealed record NormalizedAccount(string Email, string Password, string FullName, string? Phone);

    /// <summary>
    /// Kiểm tra và chuẩn hóa dữ liệu tạo tài khoản, dùng chung cho đăng ký customer, chủ tiệm và nhận lời mời.
    /// Ghi lỗi theo tên trường vào <paramref name="errors"/>; trả null khi có lỗi.
    /// </summary>
    public static NormalizedAccount? NormalizeNewAccount(RegisterRequest request, IDictionary<string, string[]> errors)
    {
        var errorCount = errors.Count;
        var email = NormalizeEmail(request.Email);
        if (email is null || !IsValidEmail(email))
        {
            errors["email"] = ["Email không hợp lệ."];
        }

        // Mật khẩu không bao giờ bị trim hay chuẩn hóa.
        var password = request.Password;
        if (!IsValidPassword(password))
        {
            errors["password"] = [PasswordRuleMessage];
        }

        var fullName = NormalizeFullName(request.FullName, errors);
        var phone = NormalizePhone(request.Phone, "phone", errors);
        return errors.Count == errorCount ? new NormalizedAccount(email!, password!, fullName!, phone) : null;
    }

    /// <summary>Họ tên bắt buộc: trim, NFC, tối đa 150 ký tự, không ký tự điều khiển.</summary>
    public static string? NormalizeFullName(string? value, IDictionary<string, string[]> errors)
    {
        var fullName = value?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrEmpty(fullName) || fullName.Length > FullNameMaxLength || fullName.Any(char.IsControl))
        {
            errors["fullName"] = [$"Họ tên là bắt buộc và tối đa {FullNameMaxLength} ký tự."];
        }

        return fullName;
    }

    /// <summary>Số điện thoại tùy chọn: trim, rỗng thành null; sai quy tắc ghi lỗi vào <paramref name="field"/>.</summary>
    public static string? NormalizePhone(string? value, string field, IDictionary<string, string[]> errors)
    {
        var phone = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (phone is not null && (phone.Length > PhoneMaxLength || !phone.All(IsPhoneChar)))
        {
            errors[field] = [$"Số điện thoại tối đa {PhoneMaxLength} ký tự, chỉ gồm chữ số, khoảng trắng và + - ( ) ."];
        }

        return phone;
    }

    public static string PasswordRuleMessage => $"Mật khẩu phải có từ {PasswordMinLength} đến {PasswordMaxLength} ký tự.";

    /// <summary>Quy tắc mật khẩu dùng chung cho đăng ký, đặt lại và đổi mật khẩu. Mật khẩu không bị trim.</summary>
    public static bool IsValidPassword(string? password) =>
        password is not null && password.Length >= PasswordMinLength && password.Length <= PasswordMaxLength;

    /// <summary>Trim, Unicode NFC, chữ thường bất biến. Dùng chung cho đăng ký và đăng nhập.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public static bool IsValidEmail(string email) =>
        email.Length <= EmailMaxLength
        && !email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && MailAddress.TryCreate(email, out var address)
        && address.Address == email
        && string.IsNullOrEmpty(address.DisplayName);

    private static bool IsPhoneChar(char c) => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '(' or ')' or '.';

    private static UserDto ToDto(UserAccount account) =>
        new(account.Id, account.Email, account.FullName, account.Phone, account.Role, account.EmailVerified);
}
