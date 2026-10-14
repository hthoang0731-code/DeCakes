using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeCakes.Infrastructure;

public static class DependencyInjection
{
    // Chưa đăng ký DbContext hay kết nối database ở bước tạo khung.
    // Khi có DbContext: đọc chuỗi kết nối "DeCakesDb" từ configuration (user-secrets/biến môi trường).
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        return services;
    }
}
