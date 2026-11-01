using DeCakes.Api.Authentication;
using DeCakes.Application.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = AuthPolicies.Admin)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminController(IAdminService service) : ControllerBase
{
    [HttpGet("dashboard")]
    [ProducesResponseType<AdminDashboardDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard([FromQuery] bool? isDemo, CancellationToken cancellationToken) =>
        Ok(await service.DashboardAsync(isDemo, cancellationToken));

    [HttpGet("users")]
    [ProducesResponseType<AdminPage<AdminUserDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Users(
        [FromQuery] string? search, [FromQuery] string? role, [FromQuery] string? status,
        [FromQuery] bool? isDemo, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        ToHttp(await service.ListUsersAsync(new(search, role, status, isDemo), page, pageSize, cancellationToken));

    // No route type constraint: malformed/overflow ids reach model binding and return 400.
    [HttpGet("users/{id}")]
    [ProducesResponseType<AdminUserDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUser(long id, CancellationToken cancellationToken) =>
        ToHttp(await service.GetUserAsync(id, cancellationToken));

    [HttpGet("bakeries")]
    [ProducesResponseType<AdminPage<AdminBakeryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Bakeries(
        [FromQuery] string? search, [FromQuery] string? profileStatus, [FromQuery] bool? isDemo,
        [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken) =>
        ToHttp(await service.ListBakeriesAsync(new(search, profileStatus, isDemo), page, pageSize, cancellationToken));

    [HttpGet("bakeries/{id}")]
    [ProducesResponseType<AdminBakeryDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Bakery(long id, CancellationToken cancellationToken) =>
        ToHttp(await service.GetBakeryAsync(id, cancellationToken));

    private IActionResult ToHttp<T>(AdminResult<T> result) => result switch
    {
        AdminResult<T>.Ok ok => Ok(ok.Value),
        AdminResult<T>.Invalid invalid => ValidationProblem(new ValidationProblemDetails(
            invalid.Errors.ToDictionary(e => e.Key, e => e.Value))),
        AdminResult<T>.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "Không tìm thấy dữ liệu."),
        _ => throw new InvalidOperationException("Kết quả Admin không hợp lệ."),
    };
}
