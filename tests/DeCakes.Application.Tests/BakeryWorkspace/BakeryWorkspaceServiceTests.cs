using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.DesignRequests;

namespace DeCakes.Application.Tests.BakeryWorkspace;

public class BakeryWorkspaceServiceTests
{
    private readonly FakeStore _store = new();
    private readonly BakeryWorkspaceService _service;

    public BakeryWorkspaceServiceTests() => _service = new BakeryWorkspaceService(_store);

    private static CancellationToken Ct => CancellationToken.None;

    [Theory]
    [InlineData("open", null, null, null, "status")]
    [InlineData("Pending", null, null, null, "status")]
    [InlineData(null, "pending", null, null, "requestStatus")]
    [InlineData(null, null, 0, null, "page")]
    [InlineData(null, null, null, 0, "pageSize")]
    [InlineData(null, null, null, 101, "pageSize")]
    public async Task List_rejects_invalid_parameters_without_querying(string? status, string? requestStatus, int? page, int? pageSize, string field)
    {
        var result = await _service.ListInboxAsync(7, 9, status, requestStatus, page, pageSize, Ct);

        Assert.Contains(field, Assert.IsType<BakeryInboxListResult.ValidationFailed>(result).Errors.Keys);
        Assert.Null(_store.LastList);
    }

    [Fact]
    public async Task List_uses_defaults_and_trims_filters()
    {
        _store.Member = true;

        Assert.IsType<BakeryInboxListResult.Ok>(await _service.ListInboxAsync(7, 9, " ", "", null, null, Ct));
        Assert.Equal((7L, 9L, (string?)null, (string?)null, 1, 20), _store.LastList);

        Assert.IsType<BakeryInboxListResult.Ok>(await _service.ListInboxAsync(7, 9, " viewed ", "open", 3, 100, Ct));
        Assert.Equal((7L, 9L, "viewed", "open", 3, 100), _store.LastList);
    }

    [Fact]
    public async Task List_for_non_member_is_bakery_not_found()
    {
        Assert.IsType<BakeryInboxListResult.BakeryNotFound>(await _service.ListInboxAsync(7, 9, null, null, null, null, Ct));
    }

    [Fact]
    public async Task Detail_distinguishes_bakery_and_request_not_found()
    {
        Assert.IsType<BakeryInboxRequestResult.BakeryNotFound>(await _service.GetInboxRequestAsync(7, 9, 1, Ct));

        _store.Member = true;
        Assert.IsType<BakeryInboxRequestResult.RequestNotFound>(await _service.GetInboxRequestAsync(7, 9, 1, Ct));

        _store.Request = new BakeryInboxRequestDto(1, "open", "T", "Ý", null, 1m, "VND", null, null, null, null,
            DateTime.UtcNow, null, "pending", null, DateTime.UtcNow, null);
        Assert.Same(_store.Request, Assert.IsType<BakeryInboxRequestResult.Ok>(await _service.GetInboxRequestAsync(7, 9, 1, Ct)).Request);
        Assert.Equal((7L, 9L, 1L), _store.LastFind);
    }

    private sealed class FakeStore : IBakeryWorkspaceStore
    {
        public bool Member { get; set; }

        public BakeryInboxRequestDto? Request { get; set; }

        public (long, long, string?, string?, int, int)? LastList { get; private set; }

        public (long, long, long)? LastFind { get; private set; }

        public Task<IReadOnlyList<WorkspaceBakeryDto>> ListMemberBakeriesAsync(long userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkspaceBakeryDto>>([]);

        public Task<bool> IsActiveMemberAsync(long userId, long bakeryId, CancellationToken cancellationToken) => Task.FromResult(Member);

        public Task<PagedResult<BakeryInboxItemDto>?> ListInboxAsync(
            long userId, long bakeryId, string? status, string? requestStatus, int page, int pageSize, CancellationToken cancellationToken)
        {
            LastList = (userId, bakeryId, status, requestStatus, page, pageSize);
            return Task.FromResult(Member ? new PagedResult<BakeryInboxItemDto>([], page, pageSize, 0) : null);
        }

        public Task<BakeryInboxRequestDto?> FindInboxRequestAsync(long userId, long bakeryId, long requestId, CancellationToken cancellationToken)
        {
            LastFind = (userId, bakeryId, requestId);
            return Task.FromResult(Member ? Request : null);
        }
    }
}
