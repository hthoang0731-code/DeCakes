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
public class AuthController(IAccountService accounts, IAntiforgery antiforgery) : ControllerBase
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
        return result switch
        {
            // Chưa tự đăng nhập sau khi đăng ký.
            RegisterResult.Created created => StatusCode(StatusCodes.Status201Created, created.User),
            RegisterResult.ValidationFailed failed => ValidationProblem(new ValidationProblemDetails(ToDictionary(failed.Errors))),
            RegisterResult.EmailTaken => Problem(statusCode: StatusCodes.Status409Conflict, title: "Email đã được sử dụng."),
            _ => throw new InvalidOperationException("Kết quả đăng ký không xác định."),
        };
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
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    AuthenticationSetup.CreatePrincipal(succeeded.User),
                    new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
                return Ok(succeeded.User);
            case LoginResult.ValidationFailed failed:
                return ValidationProblem(new ValidationProblemDetails(ToDictionary(failed.Errors)));
            case LoginResult.InvalidCredentials:
                return Problem(statusCode: StatusCodes.Status401Unauthorized, title: InvalidCredentialsTitle);
            default:
                throw new InvalidOperationException("Kết quả đăng nhập không xác định.");
        }
    }

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
        // User đã được kiểm tra (tồn tại, active, role khớp) khi xác thực cookie ở request này.
        return HttpContext.Items[AuthDefaults.CurrentUserItemKey] is UserDto user ? user : Unauthorized();
    }

    private static Dictionary<string, string[]> ToDictionary(IReadOnlyDictionary<string, string[]> errors) =>
        errors.ToDictionary(pair => pair.Key, pair => pair.Value);
}
