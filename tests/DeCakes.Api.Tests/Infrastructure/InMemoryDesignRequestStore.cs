using DeCakes.Application.DesignRequests;
using DeCakes.Domain.DesignRequests;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>
/// Store yêu cầu trong bộ nhớ cho kiểm thử API. Gửi tiệm mô phỏng thứ tự kiểm tra của sp_send_request_to_bakery;
/// hành vi thật của procedure được kiểm thử riêng trên MySQL (DeCakes.Infrastructure.Tests).
/// Dữ liệu cũng là nguồn cho InMemoryBakeryWorkspaceStore (inbox của tiệm).
/// </summary>
public sealed class InMemoryDesignRequestStore(TimeProvider time) : IDesignRequestStore
{
    private readonly object _gate = new();
    private readonly List<Stored> _requests = [];
    private readonly List<SentRow> _sent = [];
    private readonly Dictionary<long, (string Name, string ProfileStatus)> _bakeries = [];
    private long _nextId = 1;
    private long _nextSentId = 1;

    private sealed record Stored(long Id, long CustomerId, DesignRequestValues Values, string Status, DateTime CreatedAt, DateTime UpdatedAt);

    private sealed record SentRow(long Id, long RequestId, long BakeryId, string Status, string? DeclineReason, DateTime SentAt, DateTime? RespondedAt);

    /// <summary>Một dòng request_bakeries kèm yêu cầu, như inbox của tiệm đọc được.</summary>
    internal sealed record InboxRow(long Id, DesignRequestDto Request, string Status, string? DeclineReason, DateTime SentAt, DateTime? RespondedAt);

    public HashSet<long> AvailableCakes { get; } = [];

    /// <summary>Ép kết quả gửi tiệm (để kiểm thử ánh xạ HTTP của mọi mã).</summary>
    public SendToBakeryOutcome? ForcedSendOutcome { get; set; }

    public bool Fail { get; set; }

    public void AddBakery(long id, string name, bool active = true) => AddBakery(id, name, active ? "active" : "suspended");

    public void AddBakery(long id, string name, string profileStatus)
    {
        lock (_gate) _bakeries[id] = (name, profileStatus);
    }

    public string StatusOf(long id)
    {
        lock (_gate) return _requests.Single(r => r.Id == id).Status;
    }

    public long CustomerOf(long id)
    {
        lock (_gate) return _requests.Single(r => r.Id == id).CustomerId;
    }

    /// <summary>Mô phỏng tiệm phản hồi (thao tác ghi của tiệm chưa triển khai).</summary>
    public void SetSendStatus(long requestId, long bakeryId, string status, string? declineReason = null)
    {
        lock (_gate)
        {
            var index = _sent.FindIndex(s => s.RequestId == requestId && s.BakeryId == bakeryId);
            var responded = status is RequestBakeryStatuses.Quoted or RequestBakeryStatuses.Declined ? Now() : (DateTime?)null;
            _sent[index] = _sent[index] with { Status = status, DeclineReason = declineReason, RespondedAt = responded };
        }
    }

    public void SetRequestStatus(long requestId, string status)
    {
        lock (_gate)
        {
            var index = _requests.FindIndex(r => r.Id == requestId);
            _requests[index] = _requests[index] with { Status = status };
        }
    }

    internal (string Name, string ProfileStatus)? Bakery(long id)
    {
        lock (_gate) return _bakeries.TryGetValue(id, out var bakery) ? bakery : null;
    }

    internal IReadOnlyList<InboxRow> InboxRows(long bakeryId)
    {
        lock (_gate)
        {
            return _sent.Where(s => s.BakeryId == bakeryId)
                .Select(s => new InboxRow(s.Id, ToDto(_requests.Single(r => r.Id == s.RequestId)), s.Status, s.DeclineReason, s.SentAt, s.RespondedAt))
                .ToList();
        }
    }

    private DateTime Now()
    {
        var t = time.GetUtcNow().UtcDateTime;
        return new DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    public Task<long> CreateDraftAsync(long customerId, DesignRequestValues values, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var id = _nextId++;
            _requests.Add(new Stored(id, customerId, values, DesignRequestStatuses.Draft, Now(), Now()));
            return Task.FromResult(id);
        }
    }

    public Task<DesignRequestDto?> FindForCustomerAsync(long requestId, long customerId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var r = _requests.FirstOrDefault(x => x.Id == requestId && x.CustomerId == customerId);
            return Task.FromResult(r is null ? null : ToDto(r));
        }
    }

    public Task<PagedResult<DesignRequestSummaryDto>> ListForCustomerAsync(
        long customerId, string? status, int page, int pageSize, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var matching = _requests.Where(r => r.CustomerId == customerId && (status is null || r.Status == status))
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).ToList();
            var items = matching.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => new DesignRequestSummaryDto(r.Id, r.Status, r.Values.Title, r.Values.BudgetMin, r.Values.BudgetMax, "VND",
                    r.Values.PickupAtUtc, r.CreatedAt, r.UpdatedAt))
                .ToList();
            return Task.FromResult(new PagedResult<DesignRequestSummaryDto>(items, page, pageSize, matching.Count));
        }
    }

    public Task<UpdateDraftOutcome> UpdateDraftAsync(long requestId, long customerId, DesignRequestValues values, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var index = _requests.FindIndex(x => x.Id == requestId && x.CustomerId == customerId);
            if (index < 0) return Task.FromResult(UpdateDraftOutcome.NotFound);
            if (_requests[index].Status != DesignRequestStatuses.Draft) return Task.FromResult(UpdateDraftOutcome.NotDraft);
            _requests[index] = _requests[index] with { Values = values, UpdatedAt = Now() };
            return Task.FromResult(UpdateDraftOutcome.Updated);
        }
    }

    public Task<bool> IsCakeAvailableForInspirationAsync(long cakeId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult(AvailableCakes.Contains(cakeId));
    }

    public Task<SendToBakeryOutcome> SendToBakeryAsync(long requestId, long customerId, long bakeryId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        if (ForcedSendOutcome is { } forced) return Task.FromResult(forced);

        lock (_gate)
        {
            var index = _requests.FindIndex(x => x.Id == requestId);
            if (index < 0 || _requests[index].CustomerId != customerId) return Task.FromResult(SendToBakeryOutcome.RequestNotFound);
            var request = _requests[index];
            if (request.Status is not (DesignRequestStatuses.Draft or DesignRequestStatuses.Open)) return Task.FromResult(SendToBakeryOutcome.RequestNotOpen);
            if (_sent.Any(s => s.RequestId == requestId && s.BakeryId == bakeryId)) return Task.FromResult(SendToBakeryOutcome.AlreadySent);
            if (!_bakeries.TryGetValue(bakeryId, out var bakery)) return Task.FromResult(SendToBakeryOutcome.BakeryNotFound);
            if (bakery.ProfileStatus != "active") return Task.FromResult(SendToBakeryOutcome.BakeryNotActive);
            var now = Now();
            if (request.Values.PickupAtUtc <= now) return Task.FromResult(SendToBakeryOutcome.PickupInPast);

            _sent.Add(new SentRow(_nextSentId++, requestId, bakeryId, RequestBakeryStatuses.Pending, null, now, null));
            if (request.Status == DesignRequestStatuses.Draft)
            {
                _requests[index] = request with { Status = DesignRequestStatuses.Open, UpdatedAt = now };
            }

            return Task.FromResult(SendToBakeryOutcome.Sent);
        }
    }

    public Task<IReadOnlyList<RequestBakeryDto>?> ListBakeriesAsync(long requestId, long customerId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_requests.Any(x => x.Id == requestId && x.CustomerId == customerId)) return Task.FromResult<IReadOnlyList<RequestBakeryDto>?>(null);
            IReadOnlyList<RequestBakeryDto> list = _sent.Where(s => s.RequestId == requestId)
                .OrderBy(s => s.SentAt).ThenBy(s => s.Id)
                .Select(s => new RequestBakeryDto(s.BakeryId, _bakeries[s.BakeryId].Name, s.Status, s.DeclineReason, s.SentAt, s.RespondedAt))
                .ToList();
            return Task.FromResult<IReadOnlyList<RequestBakeryDto>?>(list);
        }
    }

    private static DesignRequestDto ToDto(Stored r) => new(
        r.Id, r.Status, r.Values.Title, r.Values.IdeaDescription, r.Values.BudgetMin, r.Values.BudgetMax, "VND",
        r.Values.SizeDescription, r.Values.Servings, r.Values.Flavor, r.Values.InscriptionText, r.Values.PickupAtUtc,
        r.Values.InspiredByCakeId, r.CreatedAt, r.UpdatedAt);

    private void ThrowIfFailing()
    {
        if (Fail) throw new InvalidOperationException("SIMULATED-DB-FAILURE host=db.internal");
    }
}
