namespace DeCakes.Domain.Accounts;

/// <summary>
/// Mục đích của token tài khoản (user_tokens.purpose, chk_user_tokens_purpose). Token của mục đích này không bao giờ
/// được chấp nhận cho mục đích khác.
/// </summary>
public static class AccountTokenPurposes
{
    public const string EmailVerification = "email_verification";
    public const string PasswordReset = "password_reset";
}
