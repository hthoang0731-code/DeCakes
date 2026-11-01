using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.BakeryOnboarding;
using DeCakes.Application.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Quyền sử dụng theo gói của một tiệm và thanh toán phí sử dụng DeCakes (không phải tiền bánh). Owner/staff xem tình
/// trạng; chỉ owner checkout và xem giao dịch. Kỳ sử dụng chỉ được cấp qua webhook đã xác minh, không qua trang quay về.
/// </summary>
[ApiController]
[Route("api/bakery-workspace/bakeries/{bakeryId:long:min(1)}/subscription")]
[Authorize(Policy = AuthPolicies.Bakery)]
public class BakerySubscriptionController(ISubscriptionService subscriptions) : ControllerBase
{
    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SubscriptionStatusDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status(long bakeryId, CancellationToken cancellationToken) =>
        await subscriptions.GetStatusAsync(UserId, bakeryId, cancellationToken) is { } status ? Ok(status) : BakeryNotFound();

    /// <summary>
    /// Owner đã xác minh email tạo link thanh toán cho gói đã chọn theo <c>{ billingCycle: month | year }</c>. Số tiền lấy từ
    /// giá gói ở server. Gói chưa định giá hoặc cổng chưa cấu hình: không tạo giao dịch.
    /// </summary>
    [HttpPost("checkout")]
    [RequireVerifiedEmail]
    [EnableRateLimiting(AuthDefaults.AuthRateLimitPolicy)]
    [ProducesResponseType<SubscriptionPaymentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Checkout(long bakeryId, CheckoutRequest request, CancellationToken cancellationToken)
    {
        var result = await subscriptions.CheckoutAsync(UserId, bakeryId, request, cancellationToken);
        if (result.Errors is not null)
        {
            return ValidationProblem(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        }

        return result.Outcome switch
        {
            CheckoutOutcome.Created => StatusCode(StatusCodes.Status201Created, result.Payment),
            CheckoutOutcome.BakeryNotFound => BakeryNotFound(),
            CheckoutOutcome.OwnerRequired => OwnerRequired(),
            CheckoutOutcome.ProfileNotActive => Coded(StatusCodes.Status409Conflict, "PROFILE_STATUS_NOT_ALLOWED", "Chỉ tiệm đã được duyệt mới mua hoặc gia hạn gói."),
            CheckoutOutcome.PlanNotSelected => Coded(StatusCodes.Status409Conflict, "PLAN_NOT_SELECTED", "Tiệm chưa chọn gói."),
            CheckoutOutcome.PlanNotAvailable => Coded(StatusCodes.Status409Conflict, "PLAN_NOT_AVAILABLE", "Gói đã chọn không còn mở đăng ký."),
            CheckoutOutcome.PlanNotPriced => Coded(StatusCodes.Status409Conflict, "PLAN_NOT_PRICED", "Gói chưa có giá cho chu kỳ này."),
            CheckoutOutcome.PaymentNotConfigured => Coded(StatusCodes.Status503ServiceUnavailable, "PAYMENT_NOT_CONFIGURED", "Thanh toán chưa được mở."),
            CheckoutOutcome.ProviderError => Coded(StatusCodes.Status502BadGateway, "PAYMENT_PROVIDER_ERROR", "Không tạo được link thanh toán. Thử lại sau."),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other}"),
        };
    }

    /// <summary>Trạng thái giao dịch theo dữ liệu đã xác minh ở server (dùng sau khi quay về từ trang thanh toán).</summary>
    [HttpGet("payments/{paymentId:long:min(1)}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType<SubscriptionPaymentDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Payment(long bakeryId, long paymentId, CancellationToken cancellationToken)
    {
        var (outcome, payment) = await subscriptions.GetPaymentAsync(UserId, bakeryId, paymentId, cancellationToken);
        return outcome switch
        {
            OwnerActionOutcome.Succeeded when payment is not null => Ok(payment),
            OwnerActionOutcome.Succeeded => Coded(StatusCodes.Status404NotFound, "PAYMENT_NOT_FOUND", "Không tìm thấy giao dịch."),
            OwnerActionOutcome.OwnerRequired => OwnerRequired(),
            _ => BakeryNotFound(),
        };
    }

    private ObjectResult BakeryNotFound() => Coded(StatusCodes.Status404NotFound, "BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh.");

    private ObjectResult OwnerRequired() => Coded(StatusCodes.Status403Forbidden, "OWNER_REQUIRED", "Chỉ chủ tiệm được thực hiện thao tác này.");

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
