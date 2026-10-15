using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DeCakes.Api.Authentication;

/// <summary>
/// Bắt buộc CSRF token (header X-CSRF-TOKEN + cookie antiforgery) cho mọi phương thức thay đổi dữ liệu,
/// kể cả register/login/logout. Chạy trước model binding và trước [Authorize] của action.
/// </summary>
public sealed class AntiforgeryValidationFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method))
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "CSRF token thiếu hoặc không hợp lệ.",
                Detail = $"Lấy token mới bằng GET /api/auth/csrf và gửi trong header {AuthDefaults.CsrfHeaderName}.",
            })
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
        }
    }
}
