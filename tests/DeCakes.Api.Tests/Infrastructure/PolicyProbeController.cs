using DeCakes.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>Endpoint chỉ có trong kiểm thử (nạp qua ApplicationPart) để kiểm tra policy 401/403 và CSRF.</summary>
[ApiController]
[Route("api/test-only")]
public class PolicyProbeController : ControllerBase
{
    [HttpGet("customer")]
    [Authorize(Policy = AuthPolicies.Customer)]
    public IActionResult CustomerOnly() => Ok();

    [HttpGet("bakery")]
    [Authorize(Policy = AuthPolicies.Bakery)]
    public IActionResult BakeryOnly() => Ok();

    [HttpGet("admin")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public IActionResult AdminOnly() => Ok();

    [HttpPost("authenticated-write")]
    [Authorize]
    public IActionResult AuthenticatedWrite() => NoContent();
}
