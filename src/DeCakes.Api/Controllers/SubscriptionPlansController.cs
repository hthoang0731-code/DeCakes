using DeCakes.Application.BakeryOnboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

/// <summary>Danh mục gói đang mở đăng ký. Giá null = chưa định giá (chọn được, chưa mua được).</summary>
[ApiController]
[Route("api/subscription-plans")]
[AllowAnonymous]
public class SubscriptionPlansController(IBakeryOnboardingService onboarding) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SubscriptionPlanDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await onboarding.ListPlansAsync(cancellationToken));
}
