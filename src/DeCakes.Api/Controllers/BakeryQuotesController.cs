using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Báo giá của một tiệm cho một yêu cầu: đọc lịch sử, tạo, sửa bằng phiên bản mới, rút phiên bản hiện hành.
/// Thao tác ghi cần CSRF (filter toàn cục); actor lấy từ phiên, quyền theo tiệm/yêu cầu kiểm tra ở store và procedure.
/// </summary>
[ApiController]
[Route("api/bakery-workspace/bakeries/{bakeryId:long:min(1)}/requests/{requestId:long:min(1)}/quotes")]
[Authorize(Policy = AuthPolicies.Bakery)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class BakeryQuotesController(IQuoteService quotes) : ControllerBase
{
    private long ActorUserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpGet]
    [ProducesResponseType<PagedResult<QuoteDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        long bakeryId, long requestId, [FromQuery] string? status, [FromQuery] int? page,
        [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        await quotes.ListForBakeryAsync(ActorUserId, bakeryId, requestId, status, page, pageSize, cancellationToken) switch
        {
            ListQuotesResult.ValidationFailed failed => Validation(failed.Errors),
            ListQuotesResult.Ok { Result: { Outcome: QuoteReadOutcome.Found, Page: { } data } } => Ok(data),
            ListQuotesResult.Ok ok => ReadError(ok.Result.Outcome),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet("{quoteId:long:min(1)}")]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken)
    {
        var result = await quotes.GetForBakeryAsync(ActorUserId, bakeryId, requestId, quoteId, cancellationToken);
        return result is { Outcome: QuoteReadOutcome.Found, Quote: { } quote } ? Ok(quote) : ReadError(result.Outcome);
    }

    [HttpPost]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(long bakeryId, long requestId, QuoteInput input, CancellationToken cancellationToken) =>
        Written(await quotes.CreateAsync(ActorUserId, bakeryId, requestId, input, cancellationToken), created: true);

    /// <summary>Sửa phiên bản hiện hành: tạo phiên bản mới (id mới), phiên bản cũ chuyển superseded và giữ nguyên nội dung.</summary>
    [HttpPost("{quoteId:long:min(1)}/revisions")]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revise(
        long bakeryId, long requestId, long quoteId, QuoteInput input, CancellationToken cancellationToken) =>
        Written(await quotes.ReviseAsync(ActorUserId, bakeryId, requestId, quoteId, input, cancellationToken), created: true);

    [HttpPost("{quoteId:long:min(1)}/withdraw")]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(long bakeryId, long requestId, long quoteId, CancellationToken cancellationToken) =>
        Written(await quotes.WithdrawAsync(ActorUserId, bakeryId, requestId, quoteId, cancellationToken), created: false);

    private IActionResult Written(SaveQuoteResult result, bool created) => result switch
    {
        SaveQuoteResult.Saved { Quote: var quote } when created => CreatedAtAction(
            nameof(Get), new { bakeryId = quote.BakeryId, requestId = quote.RequestId, quoteId = quote.Id }, quote),
        SaveQuoteResult.Saved saved => Ok(saved.Quote),
        SaveQuoteResult.ValidationFailed failed => Validation(failed.Errors),
        SaveQuoteResult.Rejected rejected => WriteError(rejected.Reason),
        var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
    };

    private ObjectResult WriteError(QuoteWriteOutcome outcome) => outcome switch
    {
        QuoteWriteOutcome.BakeryNotFound => Coded("BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh."),
        QuoteWriteOutcome.RequestNotFound => Coded("REQUEST_NOT_FOUND", "Không tìm thấy yêu cầu."),
        QuoteWriteOutcome.QuoteNotFound => Coded("QUOTE_NOT_FOUND", "Không tìm thấy báo giá."),
        QuoteWriteOutcome.InvalidArgument => Coded("INVALID_ARGUMENT", "Nội dung báo giá không hợp lệ.", StatusCodes.Status400BadRequest),
        QuoteWriteOutcome.InvalidPrice => Coded("QUOTE_INVALID_PRICE", "Giá báo phải lớn hơn 0.", StatusCodes.Status400BadRequest),
        // Procedure so với giờ database sau khi giữ khóa; có thể khác giờ API trong vài mili giây.
        QuoteWriteOutcome.InvalidTimes => Coded("QUOTE_INVALID_TIMES",
            "Hạn báo giá và thời gian nhận bánh phải ở tương lai.", StatusCodes.Status400BadRequest),
        QuoteWriteOutcome.DesignAssetInvalid => Coded("DESIGN_ASSET_INVALID",
            "Ảnh thiết kế không thuộc yêu cầu này hoặc tiệm không được dùng.", StatusCodes.Status400BadRequest),
        QuoteWriteOutcome.RequestNotOpen => CodedConflict("REQUEST_NOT_OPEN", "Yêu cầu không còn nhận báo giá."),
        QuoteWriteOutcome.RequestDeclined => CodedConflict("REQUEST_DECLINED_BY_BAKERY", "Tiệm đã từ chối yêu cầu này."),
        QuoteWriteOutcome.QuoteAlreadyExists => CodedConflict("QUOTE_ALREADY_EXISTS",
            "Tiệm đã có báo giá hiện hành cho yêu cầu; hãy sửa phiên bản đó."),
        QuoteWriteOutcome.QuoteAlreadyAccepted => CodedConflict("QUOTE_ALREADY_ACCEPTED", "Khách đã chọn báo giá này."),
        QuoteWriteOutcome.QuoteNotCurrent => CodedConflict("QUOTE_NOT_CURRENT",
            "Phiên bản này không còn hiện hành; tải lại lịch sử báo giá."),
        QuoteWriteOutcome.BakeryNotActive => CodedConflict("BAKERY_NOT_ACTIVE", "Tiệm chưa hoạt động nên chưa gửi được báo giá."),
        QuoteWriteOutcome.BakerySubscriptionInactive => CodedConflict("BAKERY_SUBSCRIPTION_INACTIVE",
            "Gói của tiệm đã hết hạn; gia hạn để tạo hoặc sửa báo giá."),
        _ => throw new InvalidOperationException($"Kết quả ghi báo giá không xác định: {outcome}"),
    };

    private IActionResult ReadError(QuoteReadOutcome outcome) => outcome switch
    {
        QuoteReadOutcome.BakeryNotFound => Coded("BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh."),
        QuoteReadOutcome.RequestNotFound => Coded("REQUEST_NOT_FOUND", "Không tìm thấy yêu cầu."),
        QuoteReadOutcome.QuoteNotFound => Coded("QUOTE_NOT_FOUND", "Không tìm thấy báo giá."),
        _ => throw new InvalidOperationException($"Kết quả không mong đợi: {outcome}"),
    };

    private ObjectResult CodedConflict(string code, string title) => Coded(code, title, StatusCodes.Status409Conflict);

    private ObjectResult Coded(string code, string title, int status = StatusCodes.Status404NotFound)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }

    private IActionResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        ValidationProblem(new ValidationProblemDetails(errors.ToDictionary(entry => entry.Key, entry => entry.Value)));
}
