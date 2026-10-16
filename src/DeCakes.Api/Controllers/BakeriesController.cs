using DeCakes.Application.Bakeries;
using DeCakes.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

[ApiController]
[Route("api/bakeries")]
[AllowAnonymous]
public class BakeriesController(IBakeryCatalogService bakeries) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<CatalogPage<BakeryListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var result = await bakeries.ListAsync(page, pageSize, cancellationToken);
        return result switch
        {
            BakeryListResult.Succeeded succeeded => Ok(succeeded.Page),
            BakeryListResult.ValidationFailed failed => ValidationProblem(new ValidationProblemDetails(
                failed.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))),
            _ => throw new InvalidOperationException("Kết quả danh sách tiệm bánh không xác định."),
        };
    }

    [HttpGet("{id}")]
    [ProducesResponseType<BakeryDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetById([FromRoute] long id, CancellationToken cancellationToken)
    {
        var result = await bakeries.GetByIdAsync(id, cancellationToken);
        return result switch
        {
            BakeryDetailResult.Found found => Ok(found.Bakery),
            BakeryDetailResult.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "Không tìm thấy tiệm bánh."),
            BakeryDetailResult.ValidationFailed failed => ValidationProblem(new ValidationProblemDetails(
                failed.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))),
            _ => throw new InvalidOperationException("Kết quả chi tiết tiệm bánh không xác định."),
        };
    }
}
