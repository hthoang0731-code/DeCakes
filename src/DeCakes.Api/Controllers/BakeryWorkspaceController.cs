using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.DesignRequests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>
/// Không gian làm việc của thành viên tiệm: tiệm của tôi và inbox yêu cầu, chỉ đọc. Không phải Catalog công khai.
/// Quyền theo từng tiệm được kiểm tra trong truy vấn của store, không ở controller.
/// </summary>
[ApiController]
[Route("api/bakery-workspace/bakeries")]
[Authorize(Policy = AuthPolicies.Bakery)]
public class BakeryWorkspaceController(IBakeryWorkspaceService service) : ControllerBase
{
    // User id luôn lấy từ claim của cookie đã kiểm tra; bakeryId từ route chỉ là tham số tra cứu.
    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<WorkspaceBakeryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBakeries(CancellationToken cancellationToken) =>
        Ok(await service.ListBakeriesAsync(UserId, cancellationToken));

    [HttpGet("{bakeryId:long:min(1)}/requests")]
    [ProducesResponseType<PagedResult<BakeryInboxItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListRequests(
        long bakeryId,
        [FromQuery] string? status,
        [FromQuery] string? requestStatus,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken) =>
        await service.ListInboxAsync(UserId, bakeryId, status, requestStatus, page, pageSize, cancellationToken) switch
        {
            BakeryInboxListResult.Ok ok => Ok(ok.Page),
            BakeryInboxListResult.ValidationFailed failed =>
                ValidationProblem(new ValidationProblemDetails(failed.Errors.ToDictionary(e => e.Key, e => e.Value))),
            BakeryInboxListResult.BakeryNotFound => BakeryNotFound(),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet("{bakeryId:long:min(1)}/requests/{requestId:long:min(1)}")]
    [ProducesResponseType<BakeryInboxRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRequest(long bakeryId, long requestId, CancellationToken cancellationToken) =>
        await service.GetInboxRequestAsync(UserId, bakeryId, requestId, cancellationToken) switch
        {
            BakeryInboxRequestResult.Ok ok => Ok(ok.Request),
            BakeryInboxRequestResult.BakeryNotFound => BakeryNotFound(),
            // Không tồn tại và chưa gửi đến tiệm này dùng chung 404.
            BakeryInboxRequestResult.RequestNotFound => Coded(StatusCodes.Status404NotFound, "REQUEST_NOT_FOUND", "Không tìm thấy yêu cầu."),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    // Tiệm không tồn tại và user không phải thành viên active dùng chung 404 để không lộ sự tồn tại của tiệm.
    private ObjectResult BakeryNotFound() =>
        Coded(StatusCodes.Status404NotFound, "BAKERY_NOT_FOUND", "Không tìm thấy tiệm bánh.");

    private ObjectResult Coded(int status, string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = status };
    }
}
