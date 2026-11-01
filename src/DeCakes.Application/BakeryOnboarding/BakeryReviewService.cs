using System.Text;
using DeCakes.Domain.BakeryOnboarding;
using DeCakes.Domain.Subscriptions;

namespace DeCakes.Application.BakeryOnboarding;

/// <summary>Admin duyệt / yêu cầu sửa / từ chối hồ sơ tiệm đang chờ duyệt.</summary>
public interface IBakeryReviewService
{
    Task<ReviewResult> ReviewAsync(long adminUserId, long bakeryId, ReviewDecisionRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminReviewEventDto>?> ListEventsAsync(long bakeryId, CancellationToken cancellationToken);
}

/// <summary><c>Errors</c> khác null: dữ liệu không hợp lệ, chưa chạm store.</summary>
public sealed record ReviewResult(ReviewOutcome Outcome, IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed class BakeryReviewService(IBakeryOnboardingStore store, TimeProvider time) : IBakeryReviewService
{
    public const int ReasonMaxLength = 1000;

    public async Task<ReviewResult> ReviewAsync(
        long adminUserId, long bakeryId, ReviewDecisionRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var eventType = request.Decision?.Trim() switch
        {
            BakeryReviewDecisions.Approve => BakeryReviewEvents.Approved,
            BakeryReviewDecisions.RequestChanges => BakeryReviewEvents.ChangesRequested,
            BakeryReviewDecisions.Reject => BakeryReviewEvents.Rejected,
            _ => null,
        };
        if (eventType is null)
        {
            errors["decision"] = ["decision phải là approve, request_changes hoặc reject."];
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim().Normalize(NormalizationForm.FormC);
        if (reason is not null && (reason.Length > ReasonMaxLength || reason.Any(c => char.IsControl(c) && c is not '\n' and not '\r')))
        {
            errors["reason"] = [$"Lý do tối đa {ReasonMaxLength} ký tự."];
        }
        else if (reason is null && eventType is BakeryReviewEvents.ChangesRequested or BakeryReviewEvents.Rejected)
        {
            errors["reason"] = ["Yêu cầu sửa hoặc từ chối phải có lý do."];
        }

        if (errors.Count > 0)
        {
            return new ReviewResult(ReviewOutcome.Succeeded, errors);
        }

        return new ReviewResult(await store.ReviewAsync(
            adminUserId, bakeryId, eventType!, reason, time.GetUtcNow().UtcDateTime, SubscriptionRules.TrialLength, cancellationToken));
    }

    public Task<IReadOnlyList<AdminReviewEventDto>?> ListEventsAsync(long bakeryId, CancellationToken cancellationToken) =>
        store.ListReviewEventsAsync(bakeryId, cancellationToken);
}
