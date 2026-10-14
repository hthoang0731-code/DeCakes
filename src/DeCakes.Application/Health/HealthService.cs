namespace DeCakes.Application.Health;

public sealed class HealthService : IHealthService
{
    public HealthStatusDto GetStatus() => new("Healthy", "DeCakes.Api", DateTimeOffset.UtcNow);
}
