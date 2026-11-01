using DeCakes.Application.DesignRequests;

namespace DeCakes.Application.BakeryWorkspace;

/// <summary>
/// Đọc dữ liệu của tiệm theo góc nhìn thành viên. Mọi truy vấn tự kiểm tra tư cách thành viên active của
/// <c>userId</c>: bakeryId từ route không bao giờ được coi là bằng chứng có quyền. Không có thao tác ghi.
/// </summary>
public interface IBakeryWorkspaceStore
{
    Task<IReadOnlyList<WorkspaceBakeryDto>> ListMemberBakeriesAsync(long userId, CancellationToken cancellationToken);

    Task<bool> IsActiveMemberAsync(long userId, long bakeryId, CancellationToken cancellationToken);

    /// <summary>Null khi tiệm không tồn tại hoặc user không phải thành viên active.</summary>
    Task<PagedResult<BakeryInboxItemDto>?> ListInboxAsync(
        long userId, long bakeryId, string? status, string? requestStatus, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Null khi user không phải thành viên active, hoặc yêu cầu không tồn tại/chưa gửi đến tiệm.</summary>
    Task<BakeryInboxRequestDto?> FindInboxRequestAsync(long userId, long bakeryId, long requestId, CancellationToken cancellationToken);
}
