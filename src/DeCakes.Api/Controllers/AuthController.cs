using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.Accounts;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeCakes.Api.Controllers;

public sealed record CsrfTokenResponse(string Token, string HeaderName);

[ApiController]
[Route("api/auth")]
public class AuthController(
    IAccountService accounts,
    IAccountSecurityService security,
    IAntiforgery antiforgery,
    ILogger<AuthController> logger) : ControllerBase
{
    private const string InvalidCredentialsTitle = "Email hoặc mật khẩu không đúng.";

    /// <summary>Cấp CSRF token. Gọi lại sau mỗi lần đăng nhập hoặc đăng xuất vì token gắn với danh tính hiện tại.</summary>
    [HttpGet("csrf")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<CsrfTokenResponse>(StatusCodes.Status200OK)]
    public ActionResult<CsrfTokenResponse> GetCsrfToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return new CsrfTokenResponse(tokens.RequestToken!, AuthDefaults.CsrfHeaderName);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.RegisterAsync(request, cancellationToken);
        switch (result)
        {
            case RegisterResult.Created created:
                // Tài khoản đã tạo; email xác minh là bước kèm theo. Lỗi gửi không hủy đăng ký: người dùng đăng nhập
                // rồi gửi lại. Chưa tự đăng nhập sau khi đăng ký.
                await SendVerificationAfterRegisterAsync(created.User.Id, cancellationToken);
                return StatusCode(StatusCodes.Status201Created, created.User);
            case RegisterResult.ValidationFailed failed:
                return ValidationProblem(new ValidationProblemDetails(ToDictionary(failed.Errors)));
            case RegisterResult.EmailTaken:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Email đã được sử dụng.");
            default:
                throw new InvalidOperationException("Kết quả đăng ký không xác định.");
        }
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await accounts.LoginAsync(request, cancellationToken);
        switch (result)
        {
            case LoginResult.Succeeded succeeded:
                await SignInAsync(succeeded.User, succeeded.SessionVersion);
                return Ok(succeeded.User);
            case LoginResult.ValidationFailed failed:
                return ValidationProblem(new ValidationProblemDetails(ToDictionary(failed.Errors)));
            case LoginResult.InvalidCredentials:
                return Problem(statusCode: StatusCodes.Status401Unauthorized, title: InvalidCredentialsTitle);
            default:
                throw new InvalidOperationException("Kết quả đăng nhập không xác định.");
        }
    }

    /// <summary>
    /// Chỉ xóa cookie của trình duyệt này. Cookie ở trình duyệt khác vẫn hợp lệ tới khi hết hạn, trừ khi user bị
    /// disabled/đổi role hoặc đổi/đặt lại mật khẩu (session version).
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public ActionResult<UserDto> Me()
    {
        // User đã được kiểm tra (tồn tại, active, role và session version khớp) khi xác thực cookie ở request này.
        return HttpContext.Items[AuthDefaults.CurrentUserItemKey] is UserDto user ? user : Unauthorized();
    }

    /// <summary>Cấp link xác minh mới cho user đang đăng nhập; link cũ chưa dùng bị thu hồi.</summary>
    [HttpPost("email-verification/resend")]
    [Authorize]
    [EnableRateLimiting(AuthDefaults.RecoveryRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ResendEmailVerification(CancellationToken cancellationToken)
    {
        var outcome = await security.SendEmailVerificationAsync(UserId, cancellationToken);
        switch (outcome)
        {
            case EmailDispatchOutcome.Sent:
                return Accepted();
            case EmailDispatchOutcome.AlreadyVerified:
                return Coded(StatusCodes.Status409Conflict, "EMAIL_ALREADY_VERIFIED", "Email đã được xác minh.");
            case EmailDispatchOutcome.Throttled:
                Response.Headers.RetryAfter = ((int)AccountSecurityService.IssueCooldown.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return Coded(StatusCodes.Status429TooManyRequests, "EMAIL_RESEND_THROTTLED", "Vừa gửi email xác minh. Thử lại sau.");
            case EmailDispatchOutcome.DeliveryFailed:
                logger.LogWarning("Không gửi được email xác minh cho user {UserId}.", UserId);
                return Coded(StatusCodes.Status503ServiceUnavailable, "EMAIL_DELIVERY_UNAVAILABLE", "Chưa gửi được email. Thử lại sau.");
            case EmailDispatchOutcome.NotEligible:
                return Unauthorized();
            default:
                throw new InvalidOperationException("Kết quả gửi email không xác định.");
        }
    }

    /// <summary>
    /// Xác minh email bằng token trong link. POST (không phải GET) để trình quét link trong hộp thư không tự xác minh.
    /// Không cần đăng nhập; mọi token không dùng được đều nhận cùng một lỗi.
    /// </summary>
    [HttpPost("email-verification/confirm")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthDefaults.RecoveryRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailRequest request, CancellationToken cancellationToken) =>
        await security.ConfirmEmailAsync(request, cancellationToken) ? NoContent() : InvalidToken();

    /// <summary>Luôn trả 202 không có nội dung khi email đúng định dạng, dù tài khoản có tồn tại hay không.</summary>
    [HttpPost("password/forgot")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthDefaults.RecoveryRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        switch (await security.ForgotPasswordAsync(request, cancellationToken))
        {
            case ForgotPasswordResult.Accepted accepted:
                if (accepted.Outcome == EmailDispatchOutcome.DeliveryFailed)
                {
                    // Không ghi email hay token.
                    logger.LogWarning("Không gửi được email đặt lại mật khẩu.");
                }

                return Accepted();
            case ForgotPasswordResult.ValidationFailed failed:
                return ValidationProblem(new ValidationProblemDetails(ToDictionary(failed.Errors)));
            default:
                throw new InvalidOperationException("Kết quả quên mật khẩu không xác định.");
        }
    }

    /// <summary>Đặt mật khẩu mới bằng token; mọi phiên đã cấp của user mất hiệu lực. Không tự đăng nhập.</summary>
    [HttpPost("password/reset")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthDefaults.RecoveryRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken) =>
        await security.ResetPasswordAsync(request, cancellationToken) switch
        {
            ResetPasswordResult.Succeeded => NoContent(),
            ResetPasswordResult.ValidationFailed failed => ValidationProblem(new ValidationProblemDetails(ToDictionary(failed.Errors))),
            ResetPasswordResult.InvalidToken => InvalidToken(),
            _ => throw new InvalidOperationException("Kết quả đặt lại mật khẩu không xác định."),
        };

    /// <summary>
    /// Đổi mật khẩu của user đang đăng nhập. Mọi cookie đã cấp (mọi trình duyệt) mất hiệu lực; trình duyệt hiện tại
    /// nhận cookie mới. CSRF token hiện tại vẫn dùng được vì danh tính (user id) không đổi.
    /// </summary>
    [HttpPost("password/change")]
    [Authorize]
    [EnableRateLimiting(AuthDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        switch (await security.ChangePasswordAsync(UserId, request, cancellationToken))
        {
            case ChangePasswordResult.Succeeded succeeded:
                await SignInAsync(succeeded.User, succeeded.SessionVersion);
                return NoContent();
            case ChangePasswordResult.ValidationFailed failed:
                return ValidationProblem(new ValidationProblemDetails(ToDictionary(failed.Errors)));
            case ChangePasswordResult.CurrentPasswordInvalid:
                return Coded(StatusCodes.Status400BadRequest, "CURRENT_PASSWORD_INVALID", "Mật khẩu hiện tại không đúng.");
            case ChangePasswordResult.Conflict:
                return Coded(StatusCodes.Status409Conflict, "PASSWORD_CHANGED_CONCURRENTLY", "Mật khẩu vừa được thay đổi ở nơi khác. Đăng nhập lại.");
            default:
                throw new InvalidOperationException("Kết quả đổi mật khẩu không xác định.");
        }
    }

    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    private Task SignInAsync(UserDto user, long sessionVersion) =>
        HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            AuthenticationSetup.CreatePrincipal(user, sessionVersion),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });

    private async Task SendVerificationAfterRegisterAsync(long userId, CancellationToken cancellationToken)
    {
        try
        {
            if (await security.SendEmailVerificationAsync(userId, cancellationToken) == EmailDispatchOutcome.DeliveryFailed)
            {
                logger.LogWarning("Không gửi được email xác minh sau đăng ký cho user {UserId}.", userId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tài khoản đã được tạo: không biến thành lỗi đăng ký. Không ghi chi tiết dữ liệu.
            logger.LogError(ex, "Không cấp được token xác minh sau đăng ký cho user {UserId}.", userId);
        }
    }

    private ObjectResult InvalidToken() =>
        Coded(StatusCodes.Status400BadRequest, "TOKEN_INVALID", "Liên kết không hợp lệ hoặc đã hết hạn.");

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private static Dictionary<string, string[]> ToDictionary(IReadOnlyDictionary<string, string[]> errors) =>
        errors.ToDictionary(pair => pair.Key, pair => pair.Value);
}
