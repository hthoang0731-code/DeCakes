namespace DeCakes.Application.Health;

public interface IHealthService
{
    HealthStatusDto GetStatus();
}
