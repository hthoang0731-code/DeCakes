namespace DeCakes.Application.Health;

public sealed record HealthStatusDto(string Status, string Service, DateTimeOffset Timestamp);
