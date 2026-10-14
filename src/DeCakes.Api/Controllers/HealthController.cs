using DeCakes.Application.Health;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController(IHealthService healthService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<HealthStatusDto>(StatusCodes.Status200OK)]
    public ActionResult<HealthStatusDto> Get() => Ok(healthService.GetStatus());
}
