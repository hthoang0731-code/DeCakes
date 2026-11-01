using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Đơn của khách: chọn báo giá tạo đơn, đọc danh sách, chi tiết và đơn của một yêu cầu. Chọn báo giá cần email đã xác minh
/// và CSRF (filter toàn cục); customer id lấy từ phiên. Đơn của khách khác trả 404 như không tồn tại.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Policy = AuthPolicies.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class CustomerOrdersController(IOrderService orders) : ControllerBase
{
    private long CustomerId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    /// <summary>
    /// Chọn một phiên bản báo giá. Lặp lại hoặc sai trạng thái trả 409, không coi là thành công. Nếu mất kết nối khi chờ kết quả,
    /// không gửi lại mù: đọc GET /api/design-requests/{requestId}/order để biết đơn đã được tạo hay chưa.
    /// </summary>
    [HttpPost("design-requests/{requestId:long:min(1)}/quotes/{quoteId:long:min(1)}/accept")]
    [RequireVerifiedEmail]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Accept(long requestId, long quoteId, CancellationToken cancellationToken) =>
        await orders.AcceptAsync(CustomerId, requestId, quoteId, cancellationToken) switch
        {
            SaveOrderResult.Saved { Order: var order } => CreatedAtAction(nameof(Get), new { orderId = order.Id }, order),
            SaveOrderResult.Rejected rejected => AcceptError(rejected.Reason),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet("orders")]
    [ProducesResponseType<PagedResult<OrderDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        await orders.ListForCustomerAsync(CustomerId, status, page, pageSize, cancellationToken) switch
        {
            ListOrdersResult.ValidationFailed failed =>
                ValidationProblem(new ValidationProblemDetails(failed.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))),
            ListOrdersResult.Ok { Result.Page: { } data } => Ok(data),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet("orders/{orderId:long:min(1)}")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long orderId, CancellationToken cancellationToken) =>
        Read(await orders.GetForCustomerAsync(CustomerId, orderId, cancellationToken));

    [HttpGet("design-requests/{requestId:long:min(1)}/order")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetForRequest(long requestId, CancellationToken cancellationToken) =>
        Read(await orders.GetForCustomerRequestAsync(CustomerId, requestId, cancellationToken));

    private IActionResult Read(OrderReadResult result) => result switch
    {
        { Outcome: OrderReadOutcome.Found, Order: { } order } => Ok(order),
        { Outcome: OrderReadOutcome.RequestNotFound } => Coded(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "Không tìm thấy yêu cầu."),
        { Outcome: OrderReadOutcome.OrderNotFound } => Coded(StatusCodes.Status404NotFound, "ORDER_NOT_FOUND", "Không tìm thấy đơn."),
        _ => throw new InvalidOperationException($"Kết quả không mong đợi: {result.Outcome}"),
    };

    private ObjectResult AcceptError(OrderWriteOutcome outcome) => outcome switch
    {
        OrderWriteOutcome.WorkflowNotReady => Coded(StatusCodes.Status503ServiceUnavailable, "ORDER_WORKFLOW_NOT_READY",
            "Chức năng đặt đơn chưa sẵn sàng. Thử lại sau."),
        OrderWriteOutcome.RequestNotFound => Coded(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "Không tìm thấy yêu cầu."),
        OrderWriteOutcome.QuoteNotFound => Coded(StatusCodes.Status404NotFound, "QUOTE_NOT_FOUND", "Không tìm thấy báo giá."),
        OrderWriteOutcome.InvalidArgument => Coded(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "Tham số không hợp lệ."),
        OrderWriteOutcome.RequestNotOpen => Coded(StatusCodes.Status409Conflict, "REQUEST_NOT_OPEN",
            "Yêu cầu đã có đơn hoặc không còn mở; xem đơn của yêu cầu."),
        OrderWriteOutcome.QuoteNotAvailable => Coded(StatusCodes.Status409Conflict, "QUOTE_NOT_AVAILABLE",
            "Phiên bản báo giá này không còn được chọn; tải lại báo giá."),
        OrderWriteOutcome.QuoteExpired => Coded(StatusCodes.Status409Conflict, "QUOTE_EXPIRED", "Báo giá đã hết hạn."),
        OrderWriteOutcome.QuotePickupPassed => Coded(StatusCodes.Status409Conflict, "QUOTE_PICKUP_PASSED",
            "Thời gian nhận bánh của báo giá đã qua."),
        OrderWriteOutcome.DesignAssetInvalid => Coded(StatusCodes.Status409Conflict, "DESIGN_ASSET_INVALID",
            "Ảnh thiết kế của báo giá không còn hợp lệ."),
        OrderWriteOutcome.BakeryNotActive => Coded(StatusCodes.Status409Conflict, "BAKERY_NOT_ACTIVE", "Tiệm hiện không nhận đơn mới."),
        // Không lộ chi tiết gói/thanh toán của tiệm cho khách.
        OrderWriteOutcome.BakerySubscriptionInactive => Coded(StatusCodes.Status409Conflict, "BAKERY_SUBSCRIPTION_INACTIVE",
            "Tiệm hiện không nhận đơn mới."),
        _ => throw new InvalidOperationException($"Kết quả chọn báo giá không xác định: {outcome}"),
    };

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
