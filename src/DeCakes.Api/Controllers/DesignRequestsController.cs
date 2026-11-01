using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.DesignRequests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>Yêu cầu đặt bánh của khách. Mọi endpoint chỉ cho customer; thao tác ghi cần CSRF (filter toàn cục).</summary>
[ApiController]
[Route("api/design-requests")]
[Authorize(Policy = AuthPolicies.Customer)]
public class DesignRequestsController(IDesignRequestService service) : ControllerBase
{
    // Customer id luôn lấy từ claim của cookie đã kiểm tra, không lấy từ body hay query.
    private long CustomerId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpPost]
    [RequireVerifiedEmail]
    [ProducesResponseType<DesignRequestDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(DesignRequestInput input, CancellationToken cancellationToken) =>
        await service.CreateAsync(CustomerId, input, cancellationToken) switch
        {
            DesignRequestResult.Created created => CreatedAtAction(nameof(Get), new { id = created.Request.Id }, created.Request),
            DesignRequestResult.ValidationFailed failed => Validation(failed.Errors),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet]
    [ProducesResponseType<PagedResult<DesignRequestSummaryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        await service.ListAsync(CustomerId, status, page, pageSize, cancellationToken) switch
        {
            ListDesignRequestsResult.Ok ok => Ok(ok.Page),
            ListDesignRequestsResult.ValidationFailed failed => Validation(failed.Errors),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet("{id:long:min(1)}")]
    [ProducesResponseType<DesignRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken) =>
        await service.GetAsync(CustomerId, id, cancellationToken) is { } request ? Ok(request) : RequestNotFound();

    [HttpPut("{id:long:min(1)}")]
    [ProducesResponseType<DesignRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(long id, DesignRequestInput input, CancellationToken cancellationToken) =>
        await service.UpdateAsync(CustomerId, id, input, cancellationToken) switch
        {
            DesignRequestResult.Updated updated => Ok(updated.Request),
            DesignRequestResult.ValidationFailed failed => Validation(failed.Errors),
            DesignRequestResult.NotFound => RequestNotFound(),
            DesignRequestResult.NotDraft => Coded(StatusCodes.Status409Conflict, "REQUEST_NOT_DRAFT",
                "Chỉ sửa được yêu cầu khi còn draft (chưa gửi tiệm nào)."),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpPost("{id:long:min(1)}/bakeries/{bakeryId:long:min(1)}")]
    [RequireVerifiedEmail]
    [ProducesResponseType<RequestBakeryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SendToBakery(long id, long bakeryId, CancellationToken cancellationToken)
    {
        var result = await service.SendToBakeryAsync(CustomerId, id, bakeryId, cancellationToken);
        if (result is SendToBakeryResult.Sent sent)
        {
            return CreatedAtAction(nameof(ListBakeries), new { id }, sent.Bakery);
        }

        return ((SendToBakeryResult.Failed)result).Reason switch
        {
            SendToBakeryOutcome.InvalidArgument => Coded(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "Tham số không hợp lệ."),
            SendToBakeryOutcome.RequestNotFound => RequestNotFound(),
            SendToBakeryOutcome.BakeryNotFound => Coded(StatusCodes.Status404NotFound, "BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh."),
            SendToBakeryOutcome.AlreadySent => Coded(StatusCodes.Status409Conflict, "REQUEST_ALREADY_SENT", "Yêu cầu đã được gửi đến tiệm này."),
            SendToBakeryOutcome.RequestNotOpen => Coded(StatusCodes.Status409Conflict, "REQUEST_NOT_OPEN", "Yêu cầu không còn ở trạng thái cho phép gửi tiệm."),
            SendToBakeryOutcome.PickupInPast => Coded(StatusCodes.Status409Conflict, "REQUEST_PICKUP_IN_PAST", "Thời gian nhận bánh đã qua; tạo yêu cầu mới."),
            SendToBakeryOutcome.BakeryNotActive => Coded(StatusCodes.Status409Conflict, "BAKERY_NOT_ACTIVE", "Tiệm hiện không nhận yêu cầu mới."),
            SendToBakeryOutcome.AccountNotAllowed => Coded(StatusCodes.Status403Forbidden, "ACCOUNT_NOT_ALLOWED", "Tài khoản không được phép gửi yêu cầu."),
            // Tiệm hết thời hạn gói: không nhận yêu cầu mới (không phải lỗi của khách, không lộ chi tiết thanh toán).
            SendToBakeryOutcome.BakerySubscriptionInactive => Coded(StatusCodes.Status409Conflict, "BAKERY_SUBSCRIPTION_INACTIVE",
                "Tiệm hiện không nhận yêu cầu mới."),
            var other => throw new InvalidOperationException($"Kết quả gửi tiệm không xác định: {other}"),
        };
    }

    [HttpGet("{id:long:min(1)}/bakeries")]
    [ProducesResponseType<IReadOnlyList<RequestBakeryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListBakeries(long id, CancellationToken cancellationToken) =>
        await service.ListBakeriesAsync(CustomerId, id, cancellationToken) is { } bakeries ? Ok(bakeries) : RequestNotFound();

    // Không tồn tại và thuộc khách khác dùng chung 404 để không lộ sự tồn tại của yêu cầu.
    private ObjectResult RequestNotFound() =>
        Coded(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "Không tìm thấy yêu cầu.");

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private IActionResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(e => e.Key, e => e.Value)));
}
