namespace DeCakes.Application.Bakeries;

public sealed record BakeryListItemDto(long Id, string Name, string Slug, string? City);

public sealed record BakeryDetailDto(
    long Id, string Name, string Slug, string? City, string? Description, string? AddressLine, string? Phone);
