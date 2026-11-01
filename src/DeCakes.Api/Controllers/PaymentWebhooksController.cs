using System.Text;
using DeCakes.Api.Authentication;
using DeCakes.Application.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Webhook kết quả thanh toán phí sử dụng từ payOS. Không có CSRF/cookie: thay bằng xác minh chữ ký HMAC trên nội dung
/// và đối chiếu giao dịch dưới khóa. Trang return URL của trình duyệt không bao giờ được coi là bằng chứng thanh toán.
/// </summary>
[ApiController]
[Route("api/payments/payos/webhook")]
[AllowAnonymous]
public sealed class PaymentWebhooksController(ISubscriptionService subscriptions, ILogger<PaymentWebhooksController> logger) : ControllerBase
{
    private const int MaxBodyBytes = 64 * 1024;

    [HttpPost]
    [SignedWebhook]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        var result = await subscriptions.HandleWebhookAsync(body, cancellationToken);
        switch (result.Verification)
        {
            case WebhookVerificationStatus.NotConfigured:
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            case WebhookVerificationStatus.InvalidSignature:
                logger.LogWarning("Webhook thanh toán có chữ ký không hợp lệ bị từ chối.");
                return Unauthorized();
            case WebhookVerificationStatus.NotPaid:
                return Ok(new { success = true });
            case WebhookVerificationStatus.Paid:
                if (result.Applied is PaymentApplyOutcome.Mismatch or PaymentApplyOutcome.UnknownPayment)
                {
                    // Chỉ ghi mã đơn, không ghi số tài khoản/tên người chuyển.
                    logger.LogWarning("Webhook thanh toán cần đối soát: {Outcome}, mã đơn {OrderCode}.", result.Applied, result.OrderCode);
                }

                // 2xx cho mọi kết quả đã xác minh để nhà cung cấp không gửi lại; lặp lại không đổi dữ liệu.
                return Ok(new { success = true });
            default:
                throw new InvalidOperationException("Kết quả webhook không xác định.");
        }
    }
}
