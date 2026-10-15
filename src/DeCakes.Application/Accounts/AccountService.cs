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
}

public abstract record RegisterResult
{
    public sealed record Created(UserDto User) : RegisterResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : RegisterResult;

    public sealed record EmailTaken : RegisterResult;
}

public abstract record LoginResult
{
    public sealed record Succeeded(UserDto User) : LoginResult;

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

        var email = NormalizeEmail(request.Email);
        if (email is null || !IsValidEmail(email))
        {
            errors["email"] = ["Email không hợp lệ."];
        }

        // Mật khẩu không bao giờ bị trim hay chuẩn hóa.
        var password = request.Password;
        if (password is null || password.Length < PasswordMinLength || password.Length > PasswordMaxLength)
        {
            errors["password"] = [$"Mật khẩu phải có từ {PasswordMinLength} đến {PasswordMaxLength} ký tự."];
        }

        var fullName = request.FullName?.Trim().Normalize(NormalizationForm.FormC);
        if (string.IsNullOrEmpty(fullName) || fullName.Length > FullNameMaxLength || fullName.Any(char.IsControl))
        {
            errors["fullName"] = [$"Họ tên là bắt buộc và tối đa {FullNameMaxLength} ký tự."];
        }

        var phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
        if (phone is not null && (phone.Length > PhoneMaxLength || !phone.All(IsPhoneChar)))
        {
            errors["phone"] = [$"Số điện thoại tối đa {PhoneMaxLength} ký tự, chỉ gồm chữ số, khoảng trắng và + - ( ) ."];
        }

        if (errors.Count > 0)
        {
            return new RegisterResult.ValidationFailed(errors);
        }

        var hash = passwordHasher.Hash(password!);
        try
        {
            var id = await store.CreateCustomerAsync(new NewCustomer(email!, hash, fullName!, phone), cancellationToken);
            return new RegisterResult.Created(new UserDto(id, email!, fullName!, phone, SystemRoles.Customer));
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

        return new LoginResult.Succeeded(ToDto(credentials.Account));
    }

    public async Task<UserDto?> GetActiveUserAsync(long userId, CancellationToken cancellationToken)
    {
        var account = await store.FindByIdAsync(userId, cancellationToken);
        return account is not null && account.Status == UserStatuses.Active && SystemRoles.IsKnown(account.Role)
            ? ToDto(account)
            : null;
    }

    /// <summary>Trim, Unicode NFC, chữ thường bất biến. Dùng chung cho đăng ký và đăng nhập.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static bool IsValidEmail(string email) =>
        email.Length <= EmailMaxLength
        && !email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && MailAddress.TryCreate(email, out var address)
        && address.Address == email
        && string.IsNullOrEmpty(address.DisplayName);

    private static bool IsPhoneChar(char c) => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '(' or ')' or '.';

    private static UserDto ToDto(UserAccount account) =>
        new(account.Id, account.Email, account.FullName, account.Phone, account.Role);
}
