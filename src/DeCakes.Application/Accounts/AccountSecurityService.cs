using DeCakes.Domain.Accounts;

namespace DeCakes.Application.Accounts;

/// <summary>Xác minh email, quên/đặt lại mật khẩu và đổi mật khẩu.</summary>
public interface IAccountSecurityService
{
    /// <summary>Cấp token xác minh mới cho user đang đăng nhập (thu hồi token cũ) và gửi email.</summary>
    Task<EmailDispatchOutcome> SendEmailVerificationAsync(long userId, CancellationToken cancellationToken);

    /// <summary>False cho mọi token không dùng được; không phân biệt lý do với client.</summary>
    Task<bool> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Kết quả với client luôn như nhau dù email có tồn tại hay không; <see cref="ForgotPasswordResult.Accepted.Outcome"/>
    /// chỉ dùng để ghi log nội bộ, không trả ra HTTP.
    /// </summary>
    Task<ForgotPasswordResult> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken);

    Task<ResetPasswordResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken);

    Task<ChangePasswordResult> ChangePasswordAsync(long userId, ChangePasswordRequest request, CancellationToken cancellationToken);
}

public enum EmailDispatchOutcome
{
    Sent,
    AlreadyVerified,
    /// <summary>Đã cấp quá gần hoặc quá nhiều token trong giờ qua; không cấp/gửi thêm.</summary>
    Throttled,
    /// <summary>Đã cấp token nhưng gửi email thất bại hoặc chưa cấu hình gửi email; có thể gửi lại sau.</summary>
    DeliveryFailed,
    /// <summary>Không có user phù hợp (không tồn tại, disabled, demo); không cấp token.</summary>
    NotEligible,
}

public abstract record ForgotPasswordResult
{
    public sealed record Accepted(EmailDispatchOutcome Outcome) : ForgotPasswordResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ForgotPasswordResult;
}

public abstract record ResetPasswordResult
{
    public sealed record Succeeded : ResetPasswordResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ResetPasswordResult;

    public sealed record InvalidToken : ResetPasswordResult;
}

public abstract record ChangePasswordResult
{
    /// <summary>Phiên hiện tại cần được cấp lại cookie với <paramref name="SessionVersion"/> mới.</summary>
    public sealed record Succeeded(UserDto User, long SessionVersion) : ChangePasswordResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ChangePasswordResult;

    public sealed record CurrentPasswordInvalid : ChangePasswordResult;

    /// <summary>Hash đã đổi ở nơi khác giữa lúc xác minh và lúc ghi (vd. đặt lại mật khẩu đồng thời).</summary>
    public sealed record Conflict : ChangePasswordResult;
}

public sealed class AccountSecurityService(
    IUserAccountStore users,
    IAccountSecurityStore store,
    IPasswordHashService passwordHasher,
    IAccountEmailSender emailSender,
    TimeProvider time) : IAccountSecurityService
{
    public static readonly TimeSpan EmailVerificationLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromMinutes(30);

    // Giới hạn theo từng user và mục đích, bổ sung cho rate limit theo IP ở HTTP.
    public static readonly TimeSpan IssueCooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan IssueWindow = TimeSpan.FromHours(1);
    public const int MaxIssuesPerWindow = 5;

    public async Task<EmailDispatchOutcome> SendEmailVerificationAsync(long userId, CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(userId, cancellationToken);
        if (account is null || account.Status != UserStatuses.Active)
        {
            return EmailDispatchOutcome.NotEligible;
        }

        if (account.EmailVerified)
        {
            return EmailDispatchOutcome.AlreadyVerified;
        }

        return await IssueAndSendAsync(
            account, AccountTokenPurposes.EmailVerification, EmailVerificationLifetime,
            emailSender.SendEmailVerificationAsync, cancellationToken);
    }

    public async Task<bool> ConfirmEmailAsync(ConfirmEmailRequest request, CancellationToken cancellationToken) =>
        AccountTokens.TryHash(request.Token, out var hash)
        && await store.ConfirmEmailAsync(hash, Now(), cancellationToken);

    public async Task<ForgotPasswordResult> ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var email = AccountService.NormalizeEmail(request.Email);
        if (email is null || !AccountService.IsValidEmail(email))
        {
            return new ForgotPasswordResult.ValidationFailed(new Dictionary<string, string[]> { ["email"] = ["Email không hợp lệ."] });
        }

        var credentials = await users.FindByEmailAsync(email, cancellationToken);
        var account = credentials?.Account;
        if (account is null || account.Status != UserStatuses.Active || account.IsDemo || !SystemRoles.IsKnown(account.Role))
        {
            return new ForgotPasswordResult.Accepted(EmailDispatchOutcome.NotEligible);
        }

        var outcome = await IssueAndSendAsync(
            account, AccountTokenPurposes.PasswordReset, PasswordResetLifetime,
            emailSender.SendPasswordResetAsync, cancellationToken);
        return new ForgotPasswordResult.Accepted(outcome);
    }

    public async Task<ResetPasswordResult> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        // Kiểm tra mật khẩu trước để token không bị tiêu thụ bởi một lần gửi sai định dạng.
        if (!AccountService.IsValidPassword(request.NewPassword))
        {
            return new ResetPasswordResult.ValidationFailed(
                new Dictionary<string, string[]> { ["newPassword"] = [AccountService.PasswordRuleMessage] });
        }

        if (!AccountTokens.TryHash(request.Token, out var hash))
        {
            return new ResetPasswordResult.InvalidToken();
        }

        var newHash = passwordHasher.Hash(request.NewPassword!);
        return await store.ResetPasswordAsync(hash, newHash, Now(), cancellationToken)
            ? new ResetPasswordResult.Succeeded()
            : new ResetPasswordResult.InvalidToken();
    }

    public async Task<ChangePasswordResult> ChangePasswordAsync(
        long userId, ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(request.CurrentPassword))
        {
            errors["currentPassword"] = ["Mật khẩu hiện tại là bắt buộc."];
        }

        if (!AccountService.IsValidPassword(request.NewPassword))
        {
            errors["newPassword"] = [AccountService.PasswordRuleMessage];
        }
        else if (string.Equals(request.NewPassword, request.CurrentPassword, StringComparison.Ordinal))
        {
            errors["newPassword"] = ["Mật khẩu mới phải khác mật khẩu hiện tại."];
        }

        if (errors.Count > 0)
        {
            return new ChangePasswordResult.ValidationFailed(errors);
        }

        var credentials = await store.FindCredentialsByIdAsync(userId, cancellationToken);
        // Chặn đầu vào quá dài trước khi xác minh; luôn chạy một lần xác minh (hash giả khi không có hash).
        var check = request.CurrentPassword!.Length > AccountService.PasswordMaxLength
            ? PasswordCheck.Failed
            : passwordHasher.Verify(credentials?.PasswordHash, request.CurrentPassword);
        if (credentials is null || check == PasswordCheck.Failed || credentials.Account.Status != UserStatuses.Active)
        {
            return new ChangePasswordResult.CurrentPasswordInvalid();
        }

        var newHash = passwordHasher.Hash(request.NewPassword!);
        var version = await store.ChangePasswordAsync(userId, credentials.PasswordHash!, newHash, Now(), cancellationToken);
        if (version is null)
        {
            return new ChangePasswordResult.Conflict();
        }

        var account = credentials.Account;
        return new ChangePasswordResult.Succeeded(
            new UserDto(account.Id, account.Email, account.FullName, account.Phone, account.Role, account.EmailVerified),
            version.Value);
    }

    private async Task<EmailDispatchOutcome> IssueAndSendAsync(
        UserAccount account,
        string purpose,
        TimeSpan lifetime,
        Func<string, string, string, DateTime, CancellationToken, Task> send,
        CancellationToken cancellationToken)
    {
        var now = Now();
        var stats = await store.GetIssueStatsAsync(account.Id, purpose, now - IssueWindow, cancellationToken);
        if (stats.Count >= MaxIssuesPerWindow || stats.LastIssuedAt > now - IssueCooldown)
        {
            return EmailDispatchOutcome.Throttled;
        }

        var (token, hash) = AccountTokens.Create();
        var expiresAt = now + lifetime;
        await store.IssueTokenAsync(new NewAccountToken(account.Id, purpose, hash, account.Email, now, expiresAt), cancellationToken);

        try
        {
            await send(account.Email, account.FullName, token, expiresAt, cancellationToken);
            return EmailDispatchOutcome.Sent;
        }
        catch (EmailDeliveryException)
        {
            // Token đã cấp vẫn hợp lệ nhưng không tới được người dùng; họ gửi lại sau thời gian chờ.
            return EmailDispatchOutcome.DeliveryFailed;
        }
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;
}
