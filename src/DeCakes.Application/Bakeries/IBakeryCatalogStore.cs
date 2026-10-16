namespace DeCakes.Application.Bakeries;

public interface IBakeryCatalogStore
{
    /// <summary>Chỉ đọc tiệm active, không demo, theo id tăng dần; offset/take đã được service xác thực.</summary>
    Task<IReadOnlyList<BakeryListItemDto>> ListPublicAsync(long offset, int take, CancellationToken cancellationToken);

    Task<BakeryDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken);
}
