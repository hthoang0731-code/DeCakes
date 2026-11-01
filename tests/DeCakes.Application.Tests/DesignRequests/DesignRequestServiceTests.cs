using DeCakes.Application.DesignRequests;
using DeCakes.Domain.DesignRequests;

namespace DeCakes.Application.Tests.DesignRequests;

public class DesignRequestServiceTests
{
    private readonly FakeStore _store = new();
    private readonly DesignRequestService _service;

    public DesignRequestServiceTests() =>
        _service = new DesignRequestService(_store, new FixedTime(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero)));

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task Create_with_unavailable_cake_is_validation_error_and_writes_nothing()
    {
        var result = await _service.CreateAsync(7, DesignRequestValidatorTests.Valid() with { InspiredByCakeId = 99 }, Ct);

        Assert.Contains("inspiredByCakeId", Assert.IsType<DesignRequestResult.ValidationFailed>(result).Errors.Keys);
        Assert.Equal(0, _store.Creates);
    }

    [Fact]
    public async Task Create_with_available_cake_returns_stored_request()
    {
        _store.AvailableCakes.Add(5);

        var result = await _service.CreateAsync(7, DesignRequestValidatorTests.Valid() with { InspiredByCakeId = 5 }, Ct);

        var created = Assert.IsType<DesignRequestResult.Created>(result).Request;
        Assert.Equal(DesignRequestStatuses.Draft, created.Status);
        Assert.Equal(5, created.InspiredByCakeId);
        Assert.Equal(7, _store.LastCustomerId);
    }

    [Theory]
    [InlineData("pending", null, null, "status")]
    [InlineData(null, 0, null, "page")]
    [InlineData(null, null, 0, "pageSize")]
    [InlineData(null, null, 101, "pageSize")]
    public async Task List_rejects_invalid_parameters(string? status, int? page, int? pageSize, string field)
    {
        var result = await _service.ListAsync(7, status, page, pageSize, Ct);

        Assert.Contains(field, Assert.IsType<ListDesignRequestsResult.ValidationFailed>(result).Errors.Keys);
    }

    [Fact]
    public async Task List_uses_defaults()
    {
        var result = await _service.ListAsync(7, " ", null, null, Ct);

        Assert.IsType<ListDesignRequestsResult.Ok>(result);
        Assert.Equal((7L, (string?)null, 1, 20), _store.LastList);
    }

    [Theory]
    [InlineData(UpdateDraftOutcome.NotFound, typeof(DesignRequestResult.NotFound))]
    [InlineData(UpdateDraftOutcome.NotDraft, typeof(DesignRequestResult.NotDraft))]
    [InlineData(UpdateDraftOutcome.Updated, typeof(DesignRequestResult.Updated))]
    public async Task Update_maps_store_outcome(UpdateDraftOutcome outcome, Type expected)
    {
        _store.UpdateOutcome = outcome;

        Assert.IsType(expected, await _service.UpdateAsync(7, 1, DesignRequestValidatorTests.Valid(), Ct));
    }

    [Fact]
    public async Task Update_with_invalid_input_does_not_touch_store()
    {
        var result = await _service.UpdateAsync(7, 1, DesignRequestValidatorTests.Valid() with { Title = null }, Ct);

        Assert.IsType<DesignRequestResult.ValidationFailed>(result);
        Assert.Equal(0, _store.Updates);
    }

    [Fact]
    public async Task Send_failure_is_passed_through_and_success_reads_back_bakery()
    {
        _store.SendOutcome = SendToBakeryOutcome.AlreadySent;
        Assert.Equal(SendToBakeryOutcome.AlreadySent,
            Assert.IsType<SendToBakeryResult.Failed>(await _service.SendToBakeryAsync(7, 1, 3, Ct)).Reason);

        _store.SendOutcome = SendToBakeryOutcome.Sent;
        var sent = Assert.IsType<SendToBakeryResult.Sent>(await _service.SendToBakeryAsync(7, 1, 3, Ct));
        Assert.Equal(3, sent.Bakery.BakeryId);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStore : IDesignRequestStore
    {
        public HashSet<long> AvailableCakes { get; } = [];
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public long LastCustomerId { get; private set; }
        public (long, string?, int, int) LastList { get; private set; }
        public UpdateDraftOutcome UpdateOutcome { get; set; } = UpdateDraftOutcome.Updated;
        public SendToBakeryOutcome SendOutcome { get; set; } = SendToBakeryOutcome.Sent;
        private DesignRequestValues? _last;

        public Task<long> CreateDraftAsync(long customerId, DesignRequestValues values, CancellationToken cancellationToken)
        {
            Creates++;
            LastCustomerId = customerId;
            _last = values;
            return Task.FromResult(1L);
        }

        public Task<DesignRequestDto?> FindForCustomerAsync(long requestId, long customerId, CancellationToken cancellationToken)
        {
            var v = _last ?? DesignRequestValidator.Validate(DesignRequestValidatorTests.Valid(), DateTimeOffset.MinValue).Values!;
            return Task.FromResult<DesignRequestDto?>(new DesignRequestDto(
                requestId, DesignRequestStatuses.Draft, v.Title, v.IdeaDescription, v.BudgetMin, v.BudgetMax, "VND",
                v.SizeDescription, v.Servings, v.Flavor, v.InscriptionText, v.PickupAtUtc, v.InspiredByCakeId,
                DateTime.UtcNow, DateTime.UtcNow));
        }

        public Task<PagedResult<DesignRequestSummaryDto>> ListForCustomerAsync(long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken)
        {
            LastList = (customerId, status, page, pageSize);
            return Task.FromResult(new PagedResult<DesignRequestSummaryDto>([], page, pageSize, 0));
        }

        public Task<UpdateDraftOutcome> UpdateDraftAsync(long requestId, long customerId, DesignRequestValues values, CancellationToken cancellationToken)
        {
            Updates++;
            return Task.FromResult(UpdateOutcome);
        }

        public Task<bool> IsCakeAvailableForInspirationAsync(long cakeId, CancellationToken cancellationToken) =>
            Task.FromResult(AvailableCakes.Contains(cakeId));

        public Task<SendToBakeryOutcome> SendToBakeryAsync(long requestId, long customerId, long bakeryId, CancellationToken cancellationToken) =>
            Task.FromResult(SendOutcome);

        public Task<IReadOnlyList<RequestBakeryDto>?> ListBakeriesAsync(long requestId, long customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RequestBakeryDto>?>([new RequestBakeryDto(3, "Tiệm", "pending", null, DateTime.UtcNow, null)]);
    }
}
