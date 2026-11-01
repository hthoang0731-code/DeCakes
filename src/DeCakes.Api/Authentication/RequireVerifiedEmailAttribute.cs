using DeCakes.Application.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace DeCakes.Api.Authentication;

/// <summary>
/// Thao tác chỉ dành cho user đã xác minh email: trả 403 với <c>code = EMAIL_NOT_VERIFIED</c>. Dùng user đã được kiểm tra
/// khi xác thực cookie ở chính request này (không truy vấn thêm). Đặt cùng [Authorize]: chạy sau các filter xác thực
/// và phân quyền, trước kiểm tra model để user chưa xác minh nhận 403 thay vì lỗi dữ liệu.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireVerifiedEmailAttribute : Attribute, IActionFilter, IOrderedFilter
{
    public const string Code = "EMAIL_NOT_VERIFIED";

    // ModelStateInvalidFilter của [ApiController] có Order -2000.
    public int Order => -3000;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.Items[AuthDefaults.CurrentUserItemKey] is UserDto { EmailVerified: true })
        {
            return;
        }

        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problem = factory.CreateProblemDetails(
            context.HttpContext,
            statusCode: StatusCodes.Status403Forbidden,
            title: "Cần xác minh email trước khi thực hiện thao tác này.",
            detail: "Gửi lại email xác minh bằng POST /api/auth/email-verification/resend.");
        problem.Extensions["code"] = Code;
        context.Result = new ObjectResult(problem) { StatusCode = StatusCodes.Status403Forbidden };
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
