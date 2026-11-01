using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.BakeryOnboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Admin xử lý hồ sơ tiệm đang chờ duyệt (danh sách chờ: GET /api/admin/bakeries?profileStatus=pending_review).
/// Người xử lý lấy từ phiên; thao tác ghi cần CSRF như mọi POST.
/// </summary>
[ApiController]
[Route("api/admin/bakeries/{bakeryId:long:min(1)}")]
[Authorize(Policy = AuthPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminBakeryReviewsController(IBakeryReviewService reviews) : ControllerBase
{
    /// <summary>
    /// <c>{ decision: approve | request_changes | reject, reason }</c>; lý do bắt buộc khi yêu cầu sửa/từ chối. Approve bắt
    /// đầu trial 30 ngày nếu tiệm chưa từng có trial. Chỉ hồ sơ pending_review; lần xử lý thứ hai nhận 409.
    /// </summary>
    [HttpPost("review")]
    [ProducesResponseType<IReadOnlyList<AdminReviewEventDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Review(long bakeryId, ReviewDecisionRequest request, CancellationToken cancellationToken)
    {
        var adminId = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
        var result = await reviews.ReviewAsync(adminId, bakeryId, request, cancellationToken);
        if (result.Errors is not null)
        {
            return ValidationProblem(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        }

        return result.Outcome switch
        {
            ReviewOutcome.Succeeded => Ok(await reviews.ListEventsAsync(bakeryId, cancellationToken)),
            ReviewOutcome.BakeryNotFound => Coded(StatusCodes.Status404NotFound, "BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh."),
            ReviewOutcome.NotPendingReview => Coded(StatusCodes.Status409Conflict, "BAKERY_NOT_PENDING_REVIEW", "Hồ sơ không ở trạng thái chờ duyệt."),
            ReviewOutcome.PlanNotSelected => Coded(StatusCodes.Status409Conflict, "PLAN_NOT_SELECTED", "Tiệm chưa chọn gói."),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other}"),
        };
    }

    [HttpGet("reviews")]
    [ProducesResponseType<IReadOnlyList<AdminReviewEventDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reviews(long bakeryId, CancellationToken cancellationToken) =>
        await reviews.ListEventsAsync(bakeryId, cancellationToken) is { } events
            ? Ok(events)
            : Coded(StatusCodes.Status404NotFound, "BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh.");

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
