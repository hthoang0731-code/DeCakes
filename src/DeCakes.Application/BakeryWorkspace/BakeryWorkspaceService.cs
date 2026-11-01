using DeCakes.Application.Common;
using DeCakes.Application.DesignRequests;
using DeCakes.Domain.DesignRequests;

namespace DeCakes.Application.BakeryWorkspace;

/// <summary>Inbox của tiệm, chỉ đọc. GET không đổi pending → viewed và không cập nhật thời gian.</summary>
public interface IBakeryWorkspaceService
{
    Task<IReadOnlyList<WorkspaceBakeryDto>> ListBakeriesAsync(long userId, CancellationToken cancellationToken);

    Task<BakeryInboxListResult> ListInboxAsync(
        long userId, long bakeryId, string? status, string? requestStatus, int? page, int? pageSize, CancellationToken cancellationToken);

    Task<BakeryInboxRequestResult> GetInboxRequestAsync(long userId, long bakeryId, long requestId, CancellationToken cancellationToken);
}

public abstract record BakeryInboxListResult
{
    public sealed record Ok(PagedResult<BakeryInboxItemDto> Page) : BakeryInboxListResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> Errors) : BakeryInboxListResult;

    /// <summary>Tiệm không tồn tại hoặc user không phải thành viên active.</summary>
    public sealed record BakeryNotFound : BakeryInboxListResult;
}

public abstract record BakeryInboxRequestResult
{
    public sealed record Ok(BakeryInboxRequestDto Request) : BakeryInboxRequestResult;

    /// <summary>Tiệm không tồn tại hoặc user không phải thành viên active.</summary>
    public sealed record BakeryNotFound : BakeryInboxRequestResult;

    /// <summary>Yêu cầu không tồn tại hoặc chưa được gửi đến tiệm này.</summary>
    public sealed record RequestNotFound : BakeryInboxRequestResult;
}

public sealed class BakeryWorkspaceService(IBakeryWorkspaceStore store) : IBakeryWorkspaceService
{
    public Task<IReadOnlyList<WorkspaceBakeryDto>> ListBakeriesAsync(long userId, CancellationToken cancellationToken) =>
        store.ListMemberBakeriesAsync(userId, cancellationToken);

    public async Task<BakeryInboxListResult> ListInboxAsync(
        long userId, long bakeryId, string? status, string? requestStatus, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedStatus = Normalize(status);
        if (normalizedStatus is not null && !RequestBakeryStatuses.IsKnown(normalizedStatus))
        {
            errors["status"] = ["status phải là pending, viewed, quoted hoặc declined."];
        }

        var normalizedRequestStatus = Normalize(requestStatus);
        if (normalizedRequestStatus is not null && !DesignRequestStatuses.IsKnown(normalizedRequestStatus))
        {
            errors["requestStatus"] = ["requestStatus phải là draft, open, ordered hoặc cancelled."];
        }

        var (pageNumber, size) = Paging.Normalize(page, pageSize, errors);
        if (errors.Count > 0)
        {
            return new BakeryInboxListResult.ValidationFailed(errors);
        }

        var result = await store.ListInboxAsync(
            userId, bakeryId, normalizedStatus, normalizedRequestStatus, pageNumber, size, cancellationToken);
        return result is null ? new BakeryInboxListResult.BakeryNotFound() : new BakeryInboxListResult.Ok(result);
    }

    public async Task<BakeryInboxRequestResult> GetInboxRequestAsync(
        long userId, long bakeryId, long requestId, CancellationToken cancellationToken)
    {
        if (await store.FindInboxRequestAsync(userId, bakeryId, requestId, cancellationToken) is { } request)
        {
            return new BakeryInboxRequestResult.Ok(request);
        }

        // Truy vấn đọc đã tự kiểm tra thành viên; lần kiểm tra này chỉ để chọn mã lỗi.
        return await store.IsActiveMemberAsync(userId, bakeryId, cancellationToken)
            ? new BakeryInboxRequestResult.RequestNotFound()
            : new BakeryInboxRequestResult.BakeryNotFound();
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
