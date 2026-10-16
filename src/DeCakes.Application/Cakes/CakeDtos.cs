namespace DeCakes.Application.Cakes;

public sealed record CakeListItemDto(long Id, long BakeryId, string Name, decimal? ReferencePrice, string Currency);

public sealed record CakeDetailDto(
    long Id, long BakeryId, string Name, decimal? ReferencePrice, string Currency,
    string? Description, string? Flavor, string? SizeLabel);
