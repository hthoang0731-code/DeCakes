using DeCakes.Api.Authentication;
using DeCakes.Application.Health;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

// Health không phụ thuộc database: không kiểm tra phiên đăng nhập, kể cả khi request mang cookie.
[ApiController]
[Route("api/health")]
[AllowAnonymous]
[SkipSessionValidation]
public class HealthController(IHealthService healthService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<HealthStatusDto>(StatusCodes.Status200OK)]
    public ActionResult<HealthStatusDto> Get() => Ok(healthService.GetStatus());
}
