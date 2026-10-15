# Kiểm thử

Chạy: `dotnet test` tại thư mục repository. Không cần và không dùng database.

| Project | Phạm vi |
| --- | --- |
| `DeCakes.Application.Tests` | Unit test `AccountService` với store và hasher giả: chuẩn hóa email, kiểm tra độ dài, mật khẩu không trim, trùng email, các nhánh đăng nhập thất bại, rehash. |
| `DeCakes.Api.Tests` | Kiểm thử API qua `WebApplicationFactory` (TestServer, scheme HTTPS), store user trong bộ nhớ và `PasswordHasher` thật: CSRF, cookie flags và hạn 2 giờ, `/me`, logout, user bị disabled/xóa/đổi role, 401/403, rate limit 429, lỗi database được che. Có controller chỉ dành cho kiểm thử (`PolicyProbeController`, nạp qua ApplicationPart) để thử policy. |

Môi trường kiểm thử API là `Testing`, nên không nạp user-secrets. Data Protection dùng khóa tạm trong bộ nhớ, thời gian của cookie dùng `FakeTimeProvider`.

Dự kiến bổ sung:

| Project | Phạm vi |
| --- | --- |
| `DeCakes.Domain.Tests` | Quy tắc nghiệp vụ (trạng thái yêu cầu, báo giá, đơn) khi có. |
| `DeCakes.Infrastructure.Tests` | SQL thật của store và gọi stored procedure, **chỉ trên database kiểm thử riêng**, không dùng Aiven hiện có. Gồm INSERT đăng ký, UPDATE rehash, lỗi trùng email (1062), OUT parameter và SQLSTATE 45000. |

Chuỗi kết nối cho test tích hợp lấy từ user-secrets hoặc biến môi trường, không đưa vào repository.
