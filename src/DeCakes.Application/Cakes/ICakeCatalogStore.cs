namespace DeCakes.Application.Cakes;

public interface ICakeCatalogStore
{
    /// <summary>Cake published, không demo, thuộc bakery active không demo; theo id tăng dần.</summary>
    Task<IReadOnlyList<CakeListItemDto>> ListPublicAsync(long offset, int take, long? bakeryId, CancellationToken cancellationToken);

    Task<CakeDetailDto?> FindPublicByIdAsync(long id, CancellationToken cancellationToken);
}
