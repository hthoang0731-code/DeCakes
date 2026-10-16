using DeCakes.Application.Cakes;
using DeCakes.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

[ApiController]
[Route("api/cakes")]
[AllowAnonymous]
public class CakesController(ICakeCatalogService cakes) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<CatalogPage<CakeListItemDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> List(
        [FromQuery] int? page, [FromQuery] int? pageSize, [FromQuery] long? bakeryId, CancellationToken cancellationToken)
    {
        var result = await cakes.ListAsync(page, pageSize, bakeryId, cancellationToken);
        return result switch
        {
            CakeListResult.Succeeded succeeded => Ok(succeeded.Page),
            CakeListResult.ValidationFailed failed => ValidationProblem(new ValidationProblemDetails(
                failed.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))),
            _ => throw new InvalidOperationException("Kết quả danh sách mẫu bánh không xác định."),
        };
    }

    [HttpGet("{id}")]
    [ProducesResponseType<CakeDetailDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetById([FromRoute] long id, CancellationToken cancellationToken)
    {
        var result = await cakes.GetByIdAsync(id, cancellationToken);
        return result switch
        {
            CakeDetailResult.Found found => Ok(found.Cake),
            CakeDetailResult.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "Không tìm thấy mẫu bánh."),
            CakeDetailResult.ValidationFailed failed => ValidationProblem(new ValidationProblemDetails(
                failed.Errors.ToDictionary(pair => pair.Key, pair => pair.Value))),
            _ => throw new InvalidOperationException("Kết quả chi tiết mẫu bánh không xác định."),
        };
    }
}
