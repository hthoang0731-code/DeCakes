using DeCakes.Application.BakeryWorkspace;
using DeCakes.Application.DesignRequests;
using DeCakes.Domain.BakeryWorkspace;

namespace DeCakes.Api.Tests.Infrastructure;

/// <summary>
/// Inbox của tiệm trong bộ nhớ, đọc chung dữ liệu với InMemoryDesignRequestStore để kiểm thử luồng khách gửi → tiệm đọc.
/// Chỉ đọc, giống store thật. Điều kiện users.status/system_role do kiểm thử MySQL thật bao phủ.
/// </summary>
public sealed class InMemoryBakeryWorkspaceStore(InMemoryDesignRequestStore requests) : IBakeryWorkspaceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(long BakeryId, long UserId), (string Role, string Status)> _members = [];

    public bool Fail { get; set; }

    public void AddMember(long bakeryId, long userId, string role = BakeryMemberRoles.Staff, string status = BakeryMemberStatuses.Active)
    {
        lock (_gate) _members[(bakeryId, userId)] = (role, status);
    }

    private bool IsMember(long userId, long bakeryId)
    {
        lock (_gate)
        {
            return requests.Bakery(bakeryId) is not null
                   && _members.TryGetValue((bakeryId, userId), out var m) && m.Status == BakeryMemberStatuses.Active;
        }
    }

    public Task<IReadOnlyList<WorkspaceBakeryDto>> ListMemberBakeriesAsync(long userId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<WorkspaceBakeryDto> list = _members
                .Where(m => m.Key.UserId == userId && m.Value.Status == BakeryMemberStatuses.Active && requests.Bakery(m.Key.BakeryId) is not null)
                .Select(m =>
                {
                    var bakery = requests.Bakery(m.Key.BakeryId)!.Value;
                    return new WorkspaceBakeryDto(m.Key.BakeryId, bakery.Name, bakery.ProfileStatus, m.Value.Role);
                })
                .OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.BakeryId)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<bool> IsActiveMemberAsync(long userId, long bakeryId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        return Task.FromResult(IsMember(userId, bakeryId));
    }

    public Task<PagedResult<BakeryInboxItemDto>?> ListInboxAsync(
        long userId, long bakeryId, string? status, string? requestStatus, int page, int pageSize, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        if (!IsMember(userId, bakeryId)) return Task.FromResult<PagedResult<BakeryInboxItemDto>?>(null);

        var matching = requests.InboxRows(bakeryId)
            .Where(r => (status is null || r.Status == status) && (requestStatus is null || r.Request.Status == requestStatus))
            .OrderByDescending(r => r.SentAt).ThenByDescending(r => r.Id)
            .ToList();
        var items = matching.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new BakeryInboxItemDto(r.Request.Id, r.Request.Title, r.Request.BudgetMin, r.Request.BudgetMax, r.Request.Currency,
                r.Request.PickupAt, r.Request.Status, r.Status, r.SentAt))
            .ToList();
        return Task.FromResult<PagedResult<BakeryInboxItemDto>?>(new PagedResult<BakeryInboxItemDto>(items, page, pageSize, matching.Count));
    }

    public Task<BakeryInboxRequestDto?> FindInboxRequestAsync(long userId, long bakeryId, long requestId, CancellationToken cancellationToken)
    {
        ThrowIfFailing();
        if (!IsMember(userId, bakeryId)) return Task.FromResult<BakeryInboxRequestDto?>(null);

        var row = requests.InboxRows(bakeryId).FirstOrDefault(r => r.Request.Id == requestId);
        if (row is null) return Task.FromResult<BakeryInboxRequestDto?>(null);

        var q = row.Request;
        return Task.FromResult<BakeryInboxRequestDto?>(new BakeryInboxRequestDto(
            q.Id, q.Status, q.Title, q.IdeaDescription, q.BudgetMin, q.BudgetMax, q.Currency, q.SizeDescription, q.Servings, q.Flavor,
            q.InscriptionText, q.PickupAt, q.InspiredByCakeId, row.Status, row.DeclineReason, row.SentAt, row.RespondedAt));
    }

    private void ThrowIfFailing()
    {
        if (Fail) throw new InvalidOperationException("SIMULATED-DB-FAILURE host=db.internal");
    }
}
