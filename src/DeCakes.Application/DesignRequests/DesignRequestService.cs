using DeCakes.Application.Common;
using DeCakes.Domain.DesignRequests;

namespace DeCakes.Application.DesignRequests;

public interface IDesignRequestService
{
    Task<DesignRequestResult> CreateAsync(long customerId, DesignRequestInput input, CancellationToken cancellationToken);

    Task<DesignRequestDto?> GetAsync(long customerId, long requestId, CancellationToken cancellationToken);

    Task<ListDesignRequestsResult> ListAsync(long customerId, string? status, int? page, int? pageSize, CancellationToken cancellationToken);

    Task<DesignRequestResult> UpdateAsync(long customerId, long requestId, DesignRequestInput input, CancellationToken cancellationToken);

    Task<SendToBakeryResult> SendToBakeryAsync(long customerId, long requestId, long bakeryId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RequestBakeryDto>?> ListBakeriesAsync(long customerId, long requestId, CancellationToken cancellationToken);
}

public abstract record DesignRequestResult
{
    public sealed record Created(DesignRequestDto Request) : DesignRequestResult;

    public sealed record Updated(DesignRequestDto Request) : DesignRequestResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : DesignRequestResult;

    /// <summary>Không tồn tại hoặc thuộc khách khác.</summary>
    public sealed record NotFound : DesignRequestResult;

    /// <summary>Yêu cầu không còn draft nên không được sửa.</summary>
    public sealed record NotDraft : DesignRequestResult;
}

public abstract record ListDesignRequestsResult
{
    public sealed record Ok(PagedResult<DesignRequestSummaryDto> Page) : ListDesignRequestsResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : ListDesignRequestsResult;
}

public abstract record SendToBakeryResult
{
    public sealed record Sent(RequestBakeryDto Bakery) : SendToBakeryResult;

    public sealed record Failed(SendToBakeryOutcome Reason) : SendToBakeryResult;
}

public sealed class DesignRequestService(IDesignRequestStore store, TimeProvider timeProvider) : IDesignRequestService
{
    public const int DefaultPageSize = Paging.DefaultPageSize;
    public const int MaxPageSize = Paging.MaxPageSize;

    public async Task<DesignRequestResult> CreateAsync(long customerId, DesignRequestInput input, CancellationToken cancellationToken)
    {
        var (values, errors) = await ValidateAsync(input, cancellationToken);
        if (values is null)
        {
            return new DesignRequestResult.ValidationFailed(errors);
        }

        var id = await store.CreateDraftAsync(customerId, values, cancellationToken);
        var created = await store.FindForCustomerAsync(id, customerId, cancellationToken)
                      ?? throw new InvalidOperationException("Không đọc lại được yêu cầu vừa tạo.");
        return new DesignRequestResult.Created(created);
    }

    public Task<DesignRequestDto?> GetAsync(long customerId, long requestId, CancellationToken cancellationToken) =>
        store.FindForCustomerAsync(requestId, customerId, cancellationToken);

    public async Task<ListDesignRequestsResult> ListAsync(
        long customerId, string? status, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        if (normalizedStatus is not null && !DesignRequestStatuses.IsKnown(normalizedStatus))
        {
            errors["status"] = ["Trạng thái phải là draft, open, ordered hoặc cancelled."];
        }

        var (pageNumber, size) = Paging.Normalize(page, pageSize, errors);
        if (errors.Count > 0)
        {
            return new ListDesignRequestsResult.ValidationFailed(errors);
        }

        return new ListDesignRequestsResult.Ok(
            await store.ListForCustomerAsync(customerId, normalizedStatus, pageNumber, size, cancellationToken));
    }

    public async Task<DesignRequestResult> UpdateAsync(
        long customerId, long requestId, DesignRequestInput input, CancellationToken cancellationToken)
    {
        var (values, errors) = await ValidateAsync(input, cancellationToken);
        if (values is null)
        {
            return new DesignRequestResult.ValidationFailed(errors);
        }

        // Kiểm tra draft và ghi nằm trong cùng một câu UPDATE ở store: không có khoảng hở với lần gửi tiệm đồng thời.
        switch (await store.UpdateDraftAsync(requestId, customerId, values, cancellationToken))
        {
            case UpdateDraftOutcome.NotFound:
                return new DesignRequestResult.NotFound();
            case UpdateDraftOutcome.NotDraft:
                return new DesignRequestResult.NotDraft();
        }

        var updated = await store.FindForCustomerAsync(requestId, customerId, cancellationToken);
        return updated is null ? new DesignRequestResult.NotFound() : new DesignRequestResult.Updated(updated);
    }

    public async Task<SendToBakeryResult> SendToBakeryAsync(
        long customerId, long requestId, long bakeryId, CancellationToken cancellationToken)
    {
        var outcome = await store.SendToBakeryAsync(requestId, customerId, bakeryId, cancellationToken);
        if (outcome != SendToBakeryOutcome.Sent)
        {
            return new SendToBakeryResult.Failed(outcome);
        }

        // Procedure không trả result set: đọc lại dòng vừa tạo.
        var bakeries = await store.ListBakeriesAsync(requestId, customerId, cancellationToken);
        var sent = bakeries?.FirstOrDefault(b => b.BakeryId == bakeryId)
                   ?? throw new InvalidOperationException("Không đọc lại được tiệm vừa gửi.");
        return new SendToBakeryResult.Sent(sent);
    }

    public Task<IReadOnlyList<RequestBakeryDto>?> ListBakeriesAsync(long customerId, long requestId, CancellationToken cancellationToken) =>
        store.ListBakeriesAsync(requestId, customerId, cancellationToken);

    private async Task<(DesignRequestValues? Values, Dictionary<string, string[]> Errors)> ValidateAsync(
        DesignRequestInput input, CancellationToken cancellationToken)
    {
        var (values, errors) = DesignRequestValidator.Validate(input, timeProvider.GetUtcNow());
        if (values?.InspiredByCakeId is { } cakeId
            && !await store.IsCakeAvailableForInspirationAsync(cakeId, cancellationToken))
        {
            errors["inspiredByCakeId"] = ["Mẫu bánh không tồn tại hoặc không còn hiển thị."];
            return (null, errors);
        }

        return (values, errors);
    }
}
