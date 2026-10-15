namespace DeCakes.Api.Authentication;

public static class AuthDefaults
{
    // Tiền tố __Host-: trình duyệt chỉ nhận cookie khi Secure, Path=/ và không có Domain.
    public const string AuthCookieName = "__Host-decakes.auth";
    public const string AntiforgeryCookieName = "__Host-decakes.csrf";
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(2);

    public const string AuthRateLimitPolicy = "auth";

    // Khóa HttpContext.Items chứa UserDto đã kiểm tra trong request hiện tại.
    public const string CurrentUserItemKey = "DeCakes.CurrentUser";
}

public static class AuthPolicies
{
    public const string Customer = "Customer";
    public const string Bakery = "Bakery";
    public const string Admin = "Admin";
}
