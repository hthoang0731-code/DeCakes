using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Đơn của một tiệm: đọc danh sách/chi tiết và xác nhận hoặc từ chối đơn chờ xác nhận. Owner/staff active có cùng quyền;
/// actor lấy từ phiên, quyền theo đúng tiệm trên route kiểm tra ở store và procedure. Quyết định cần CSRF (filter toàn cục).
/// </summary>
[ApiController]
[Route("api/bakery-workspace/bakeries/{bakeryId:long:min(1)}/orders")]
[Authorize(Policy = AuthPolicies.Bakery)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class BakeryOrdersController(IOrderService orders) : ControllerBase
{
    private long ActorUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpGet]
    [ProducesResponseType<PagedResult<OrderDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        long bakeryId, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken cancellationToken) =>
        await orders.ListForBakeryAsync(ActorUserId, bakeryId, status, page, pageSize, cancellationToken) switch
        {
            ListOrdersResult.ValidationFailed failed => Validation(failed.Errors),
            ListOrdersResult.Ok { Result: { Outcome: OrderReadOutcome.Found, Page: { } data } } => Ok(data),
            ListOrdersResult.Ok ok => ReadError(ok.Result.Outcome),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet("{orderId:long:min(1)}")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long bakeryId, long orderId, CancellationToken cancellationToken)
    {
        var result = await orders.GetForBakeryAsync(ActorUserId, bakeryId, orderId, cancellationToken);
        return result is { Outcome: OrderReadOutcome.Found, Order: { } order } ? Ok(order) : ReadError(result.Outcome);
    }

    /// <summary>
    /// Body <c>{ decision: "confirm" | "reject", reason? }</c>. Reject bắt buộc lý do (tối đa 500 ký tự); confirm không nhận lý do.
    /// Thành công trả 200 với đơn đọc lại. Quyết định lặp lại hoặc đơn không còn chờ xác nhận trả 409.
    /// </summary>
    [HttpPost("{orderId:long:min(1)}/decision")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Decide(
        long bakeryId, long orderId, OrderDecisionInput input, CancellationToken cancellationToken) =>
        await orders.DecideAsync(ActorUserId, bakeryId, orderId, input, cancellationToken) switch
        {
            SaveOrderResult.Saved saved => Ok(saved.Order),
            SaveOrderResult.ValidationFailed failed => Validation(failed.Errors),
            SaveOrderResult.Rejected rejected => DecisionError(rejected.Reason),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    private ObjectResult DecisionError(OrderWriteOutcome outcome) => outcome switch
    {
        OrderWriteOutcome.WorkflowNotReady => Coded("ORDER_WORKFLOW_NOT_READY", "Chức năng xử lý đơn chưa sẵn sàng. Thử lại sau.",
            StatusCodes.Status503ServiceUnavailable),
        OrderWriteOutcome.BakeryNotFound => Coded("BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh."),
        OrderWriteOutcome.OrderNotFound => Coded("ORDER_NOT_FOUND", "Không tìm thấy đơn."),
        OrderWriteOutcome.InvalidArgument => Coded("INVALID_ARGUMENT", "Quyết định không hợp lệ.", StatusCodes.Status400BadRequest),
        OrderWriteOutcome.RejectionReasonRequired => Coded("ORDER_REJECTION_REASON_REQUIRED", "Lý do từ chối là bắt buộc.",
            StatusCodes.Status400BadRequest),
        OrderWriteOutcome.OrderNotPending => Coded("ORDER_NOT_PENDING", "Đơn không còn chờ xác nhận; tải lại đơn.",
            StatusCodes.Status409Conflict),
        // Procedure so với giờ database sau khi giữ khóa.
        OrderWriteOutcome.OrderPickupPassed => Coded("ORDER_PICKUP_PASSED", "Đã tới giờ nhận bánh nên không xác nhận được; có thể từ chối đơn.",
            StatusCodes.Status409Conflict),
        OrderWriteOutcome.BakeryNotActive => Coded("BAKERY_NOT_ACTIVE", "Tiệm chưa hoạt động nên chưa xác nhận được đơn.",
            StatusCodes.Status409Conflict),
        _ => throw new InvalidOperationException($"Kết quả quyết định đơn không xác định: {outcome}"),
    };

    private IActionResult ReadError(OrderReadOutcome outcome) => outcome switch
    {
        OrderReadOutcome.BakeryNotFound => Coded("BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh."),
        OrderReadOutcome.OrderNotFound => Coded("ORDER_NOT_FOUND", "Không tìm thấy đơn."),
        _ => throw new InvalidOperationException($"Kết quả không mong đợi: {outcome}"),
    };

    private ObjectResult Coded(string code, string title, int status = StatusCodes.Status404NotFound)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private IActionResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(entry => entry.Key, entry => entry.Value)));
}
