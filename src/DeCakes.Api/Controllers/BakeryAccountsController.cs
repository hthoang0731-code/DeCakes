using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.Accounts;
using DeCakes.Application.BakeryOnboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Tài khoản người dùng tiệm: chủ tiệm tự đăng ký (tài khoản bakery + hồ sơ draft + owner) và nhận lời mời staff.
/// Không đăng nhập tự động; không bao giờ đổi role của tài khoản đang có.
/// </summary>
[ApiController]
[Route("api/auth")]
public class BakeryAccountsController(
    IBakeryOnboardingService onboarding,
    IBakeryStaffService staff,
    IAccountSecurityService security,
    ILogger<BakeryAccountsController> logger) : ControllerBase
{
    [HttpPost("register-bakery")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType<RegisteredBakeryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterBakery(RegisterBakeryRequest request, CancellationToken cancellationToken)
    {
        switch (await onboarding.RegisterAsync(request, cancellationToken))
        {
            case RegisterBakeryResult.Created created:
                await SendVerificationAsync(created.Registered.User.Id, cancellationToken);
                return StatusCode(StatusCodes.Status201Created, created.Registered);
            case RegisterBakeryResult.ValidationFailed failed:
                return ValidationProblem(new ValidationProblemDetails(failed.Errors.ToDictionary(e => e.Key, e => e.Value)));
            case RegisterBakeryResult.EmailTaken:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Email đã được sử dụng.");
            default:
                throw new InvalidOperationException("Kết quả đăng ký tiệm không xác định.");
        }
    }

    /// <summary>
    /// User đang đăng nhập nhận lời mời bằng chính tài khoản bakery có đúng email lời mời. Customer/admin nhận 403
    /// ROLE_NOT_ALLOWED (không tự đổi role). POST để trình quét link không tự nhận lời mời.
    /// </summary>
    [HttpPost("invitations/accept")]
    [Authorize]
    [EnableRateLimiting(AuthDefaults.RecoveryRateLimitPolicy)]
    [ProducesResponseType<AcceptedInvitationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Accept(AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        var userId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
        var result = await staff.AcceptAsync(userId, request, cancellationToken);
        return result.Status switch
        {
            AcceptInvitationStatus.Accepted => Ok(new AcceptedInvitationDto(result.BakeryId, result.UserId)),
            AcceptInvitationStatus.RoleNotAllowed => Coded(StatusCodes.Status403Forbidden, "ROLE_NOT_ALLOWED",
                "Chỉ tài khoản tiệm bánh nhận được lời mời. Dùng email khác để tạo tài khoản tiệm."),
            _ => InvalidToken(),
        };
    }

    /// <summary>Tạo tài khoản bakery mới với email của lời mời (đã xác minh bằng token) và tự đặt mật khẩu.</summary>
    [HttpPost("invitations/accept-new")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthDefaults.RecoveryRateLimitPolicy)]
    [ProducesResponseType<AcceptedInvitationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AcceptWithNewAccount(AcceptInvitationNewAccountRequest request, CancellationToken cancellationToken)
    {
        var accepted = await staff.AcceptWithNewAccountAsync(request, cancellationToken);
        if (accepted.Errors is not null)
        {
            return ValidationProblem(new ValidationProblemDetails(accepted.Errors.ToDictionary(e => e.Key, e => e.Value)));
        }

        return accepted.Result.Status switch
        {
            AcceptInvitationStatus.Accepted => StatusCode(StatusCodes.Status201Created,
                new AcceptedInvitationDto(accepted.Result.BakeryId, accepted.Result.UserId)),
            AcceptInvitationStatus.AccountExists => Coded(StatusCodes.Status409Conflict, "ACCOUNT_EXISTS",
                "Email của lời mời đã có tài khoản. Đăng nhập rồi nhận lời mời."),
            _ => InvalidToken(),
        };
    }

    private async Task SendVerificationAsync(long userId, CancellationToken cancellationToken)
    {
        try
        {
            if (await security.SendEmailVerificationAsync(userId, cancellationToken) == EmailDispatchOutcome.DeliveryFailed)
            {
                logger.LogWarning("Không gửi được email xác minh sau đăng ký tiệm cho user {UserId}.", userId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Không cấp được token xác minh sau đăng ký tiệm cho user {UserId}.", userId);
        }
    }

    private ObjectResult InvalidToken() =>
        Coded(StatusCodes.Status400BadRequest, "TOKEN_INVALID", "Lời mời không hợp lệ hoặc đã hết hạn.");

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
