namespace DeCakes.Api.Authentication;

public static class AuthDefaults
{
    // Tiền tố __Host-: trình duyệt chỉ nhận cookie khi Secure, Path=/ và không có Domain.
    public const string AuthCookieName = "__Host-decakes.auth";
    public const string AntiforgeryCookieName = "__Host-decakes.csrf";
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);

    public const string AuthRateLimitPolicy = "auth";

    // Quên/đặt lại mật khẩu, xác minh email: giới hạn theo IP chặt hơn, riêng với đăng nhập.
    public const string RecoveryRateLimitPolicy = "auth-recovery";

    // users.session_version lúc cấp cookie; lệch với database thì cookie bị từ chối (đổi/đặt lại mật khẩu).
    public const string SessionVersionClaim = "decakes:sv";

    // Khóa HttpContext.Items chứa UserDto đã kiểm tra trong request hiện tại.
    public const string CurrentUserItemKey = "DeCakes.CurrentUser";
}

public static class AuthPolicies
{
    public const string Customer = "Customer";
    public const string Bakery = "Bakery";
    public const string Admin = "Admin";
}
