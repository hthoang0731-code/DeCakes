using System.Globalization;
using System.Security.Claims;
using DeCakes.Api.Authentication;
using DeCakes.Application.DesignRequests;
using DeCakes.Application.Quotes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DeCakes.Api.Controllers;

[ApiController]
[Route("api/design-requests/{requestId:long:min(1)}/quotes")]
[Authorize(Policy = AuthPolicies.Customer)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class CustomerQuotesController(IQuoteService quotes) : ControllerBase
{
    private long CustomerId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);

    [HttpGet]
    [ProducesResponseType<PagedResult<QuoteDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        long requestId, [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken cancellationToken) =>
        await quotes.ListForCustomerAsync(CustomerId, requestId, status, page, pageSize, cancellationToken) switch
        {
            ListQuotesResult.ValidationFailed failed =>
                ValidationProblem(new ValidationProblemDetails(failed.Errors.ToDictionary(entry => entry.Key, entry => entry.Value))),
            ListQuotesResult.Ok { Result: { Outcome: QuoteReadOutcome.Found, Page: { } data } } => Ok(data),
            ListQuotesResult.Ok ok => ReadError(ok.Result.Outcome),
            var other => throw new InvalidOperationException($"Kết quả không mong đợi: {other.GetType().Name}"),
        };

    [HttpGet("{quoteId:long:min(1)}")]
    [ProducesResponseType<QuoteDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(long requestId, long quoteId, CancellationToken cancellationToken)
    {
        var result = await quotes.GetForCustomerAsync(CustomerId, requestId, quoteId, cancellationToken);
        return result is { Outcome: QuoteReadOutcome.Found, Quote: { } quote } ? Ok(quote) : ReadError(result.Outcome);
    }

    private IActionResult ReadError(QuoteReadOutcome outcome) => outcome switch
    {
        QuoteReadOutcome.RequestNotFound => Coded("REQUEST_NOT_FOUND", "Không tìm thấy yêu cầu."),
        QuoteReadOutcome.QuoteNotFound => Coded("QUOTE_NOT_FOUND", "Không tìm thấy báo giá."),
        _ => throw new InvalidOperationException($"Kết quả không mong đợi: {outcome}"),
    };

    private ObjectResult Coded(string code, string title)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: StatusCodes.Status404NotFound, title: title);
        problem.Extensions["code"] = code;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status404NotFound };
    }
}
