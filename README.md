# DeCakes Backend

Backend cho website đặt bánh theo yêu cầu: khách gửi một yêu cầu đến nhiều tiệm, nhận báo giá, chọn một báo giá và chờ tiệm xác nhận đơn. C#/.NET 10, Clean Architecture tinh gọn.

## Điều kiện

- .NET SDK 10 (kiểm tra bằng `dotnet --list-sdks`).
- `global.json` chọn SDK 10.0 stable mới nhất đã cài trên máy; không tự chuyển sang .NET 11 hoặc bản preview.
- MySQL 8.4 (`decakes_db` trên Aiven) khi bắt đầu làm việc với dữ liệu. Chưa cần ở bước khung dự án.

## Cấu trúc

```
DeCakes.slnx
src/
  DeCakes.Domain/          Khái niệm và quy tắc nghiệp vụ, không phụ thuộc HTTP/MySQL
  DeCakes.Application/     DTO, interface, service nghiệp vụ, theo chức năng
  DeCakes.Infrastructure/  Truy cập dữ liệu bằng ADO.NET (MySqlConnector), triển khai interface
  DeCakes.Api/             ASP.NET Core Web API (Controllers), cấu hình, xử lý lỗi HTTP
tests/
  DeCakes.Application.Tests/  Kiểm thử service (fake store, fake hasher)
  DeCakes.Api.Tests/          Kiểm thử API qua TestServer HTTPS và Kestrel HTTPS, store trong bộ nhớ
  DeCakes.Infrastructure.Tests/  Chốt bảo vệ + store MySQL thật trên MySQL 8.4 kiểm thử riêng (xem tests/README.md)
database/                  Script SQL đã kiểm thử (xem database/README.md)
```

Chức năng: Accounts, Bakeries, DesignRequests, Quotes, Orders. Mỗi chức năng có thư mục riêng trong Domain và Application. Controllers trong Api đặt theo cùng tên.

### Phụ thuộc giữa các project

```
Api ──► Application ──► Domain
 └────► Infrastructure ──► Application, Domain
```

Domain độc lập. Api tham chiếu Infrastructure chỉ để đăng ký dependency injection (`AddApplication()`, `AddInfrastructure(configuration)`).

### Chưa có trong khung

MediatR, CQRS, Generic Repository, Unit of Work, microservices, AI tạo ảnh, thanh toán, chat, giao hàng.

### Quy ước về database

- Schema hiện tại của `decakes_db` là cơ sở phát triển, không tạo lại.
- Truy cập dữ liệu bằng **ADO.NET với MySqlConnector 2.6.2**, kết nối `SslMode=VerifyFull` với CA Aiven. **Chưa dùng EF Core, chưa có DbContext và model scaffold** (quyết định hiện tại; provider Oracle không dùng được `VerifyFull` chỉ với CA, Pomelo chưa có bản stable cho EF Core 10).
- `IMySqlConnectionFactory` (Infrastructure/Persistence) mở một connection cho mỗi thao tác; người gọi dispose. Không giữ connection trong singleton, không kết nối khi ứng dụng khởi động. Factory từ chối cấu hình không phải `VerifyFull`, thiếu CA hoặc dùng `avnadmin`.
- Nghiệp vụ gọi các stored procedure đã có. Sáu procedure tự quản lý transaction (xem `database/STORED_PROCEDURES.md`) phải được gọi trên connection không có transaction của ứng dụng đang mở.
- Quy ước viết mã truy cập dữ liệu:
  - SQL luôn dùng parameter, không nối dữ liệu người dùng vào câu lệnh.
  - Application định nghĩa DTO và interface theo chức năng; Infrastructure thực hiện truy vấn và ánh xạ. Kiểu `MySqlConnector` không đi vào Application hoặc Domain.
  - Không Generic Repository, không Unit of Work.
  - Thao tác ghi nhiều bảng không đi qua 6 procedure trên có thể dùng transaction riêng trong Infrastructure khi triển khai sau.
- Ứng dụng không chạy migration hay thay đổi database khi khởi động.

## Restore, build, run

```
dotnet restore
dotnet build
dotnet run --project src/DeCakes.Api
```

Chạy các lệnh trên tại thư mục repository `D:\DeCakes\BE`, không phải `D:\DeCakes` hoặc thư mục home. Mở thư mục `BE` trong VS Code.

Nếu môi trường hạn chế tiến trình khiến restore/build báo thất bại với 0 lỗi, chạy tuần tự:

```
dotnet restore DeCakes.slnx --disable-parallel -m:1 -nr:false
dotnet build DeCakes.slnx --no-restore -m:1 -nr:false
```

Kiểm tra: `GET /api/health` trả HTTP 200. Endpoint này không kiểm tra database. Ở môi trường Development, OpenAPI tại `/openapi/v1.json`.

**Đăng nhập cần HTTPS**: cookie đăng nhập và cookie CSRF luôn có cờ `Secure`, trình duyệt không gửi chúng qua HTTP. Chạy profile HTTPS:

```
dotnet run --project src/DeCakes.Api --launch-profile https
```

Cổng 7215 (HTTPS); request HTTP tới 5011 được chuyển hướng sang HTTPS. Cần chứng chỉ phát triển được tin cậy: `dotnet dev-certs https --trust` (Windows sẽ hỏi xác nhận).

Kiểm thử: `dotnet test` (không kết nối database).

## Cấu hình bí mật

Không ghi mật khẩu, token, API key vào mã nguồn, `appsettings*.json`, log hay README. `appsettings.json` chỉ chứa giá trị placeholder.

Dùng user-secrets:

```
dotnet user-secrets set "ConnectionStrings:DeCakesDb" "<chuỗi kết nối>" --project src/DeCakes.Api
```

Hoặc biến môi trường (dùng `__` thay cho `:`):

```
ConnectionStrings__DeCakesDb=<chuỗi kết nối>
```

File `.env`, `*.local.env` bị `.gitignore` loại trừ. Không sao chép `decakes-test.local.env` vào repository.

Api đã có `UserSecretsId`, không cần chạy `user-secrets init`. Chỉ đặt chuỗi kết nối của tài khoản backend riêng có quyền hạn chế; không dùng tài khoản quản trị Aiven.

### Chuỗi kết nối TLS tới Aiven (cấu hình trên máy của bạn)

Chạy `dotnet run scripts/SetDbSecret.cs` (dùng MySqlConnector 2.6.2). Script hỏi host, port, tài khoản backend, đường dẫn CA và mật khẩu (nhập ẩn), dựng chuỗi bằng `MySqlConnectionStringBuilder` với `SslMode=VerifyFull` và `SslCa`, rồi ghi vào user-secrets mà không in giá trị. Các secret khác được giữ nguyên. Script từ chối `avnadmin`. Mật khẩu không đi qua tham số dòng lệnh nên không nằm trong lịch sử terminal.

Dạng chuỗi (placeholder, chỉ để tham khảo; không tự gõ có mật khẩu vào terminal):

```
Server=<host>;Port=<port>;Database=decakes_db;User Id=<tài khoản backend>;Password=<mật khẩu>;SslMode=VerifyFull;SslCa=<đường dẫn tuyệt đối tới ca.pem>
```

- Lấy host, port và CA certificate trong Aiven Console. Lưu `ca.pem` trong `certs/` (đã bị `.gitignore` loại trừ) hoặc ngoài repository.
- Không hạ `SslMode` xuống `VerifyCA`/`Required`/`Preferred` hay tắt kiểm tra chứng chỉ để vượt lỗi kết nối.
- Tài khoản phải là user MySQL riêng cho backend (xem `database/security/app_user_grants.sql`), không phải `avnadmin`.
- Secret do bản `SetDbSecret.cs` cũ (dùng MySql.Data) ghi có khóa CA tên `certificatefile`, mà MySqlConnector hiểu là chứng chỉ client. Factory từ chối chuỗi như vậy (thiếu `SslCa`). Chạy lại bản `SetDbSecret.cs` hiện tại để ghi đè.

## Xác thực (Accounts)

Dùng bảng `users` hiện có, không thêm bảng Identity. Mật khẩu hash bằng `PasswordHasher<TUser>` của Microsoft (V3, PBKDF2-HMAC-SHA512). Đăng nhập bằng cookie, không JWT hay refresh token. Giả định frontend và API cùng origin; khi phát triển có thể dùng proxy của frontend. Không bật CORS.

| Endpoint | Mô tả | CSRF | Rate limit |
| --- | --- | --- | --- |
| `GET /api/auth/csrf` | Cấp CSRF token, `Cache-Control: no-store` | | |
| `POST /api/auth/register` | `{ email, password, fullName, phone? }` → 201 với user an toàn. Chỉ tạo `customer`, `active`, `is_demo = 0`. Không tự đăng nhập | Bắt buộc | Có |
| `POST /api/auth/login` | `{ email, password }` → 200 và cookie đăng nhập | Bắt buộc | Có |
| `POST /api/auth/logout` | Xóa cookie đăng nhập → 204 | Bắt buộc | |
| `GET /api/auth/me` | User hiện tại; 401 nếu chưa đăng nhập | | |

Quy trình gọi từ frontend (cùng origin, HTTPS, gửi kèm cookie):

1. `GET /api/auth/csrf` → nhận `{ token, headerName }`. Server đặt cookie `__Host-decakes.csrf`.
2. Gửi mọi `POST`/`PUT`/`PATCH`/`DELETE` kèm header `X-CSRF-TOKEN: <token>`. Thiếu hoặc sai token → 400.
3. **Sau khi đăng nhập hoặc đăng xuất, gọi lại `GET /api/auth/csrf`**: token gắn với danh tính đang đăng nhập, token cũ không còn hợp lệ.

Hành vi chính:

- Cookie `__Host-decakes.auth`: `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, không có `Domain`. Là cookie phiên của trình duyệt (không remember-me). Ticket hết hạn tuyệt đối sau 2 giờ, không gia hạn (sliding).
- Claims chỉ gồm user id và role, lấy từ server. Mỗi request có cookie, server đọc lại user: nếu user bị xóa, bị `disabled` hoặc đổi role, cookie bị từ chối và xóa. Ngoại lệ duy nhất là `GET /api/health` (`[SkipSessionValidation]`): cookie bị bỏ qua, request được coi là ẩn danh, không truy vấn database và không xóa cookie, nên health vẫn trả 200 khi database lỗi.
- Mọi đăng nhập thất bại (không có user, chưa có/hỏng hash, sai mật khẩu, user `disabled`) trả cùng một 401. Khi không có user, hash NULL, không phải base64 hoặc sai cấu trúc V2/V3, vẫn chạy một lần xác minh trên hash giả; hash đúng cấu trúc được xác minh bằng `PasswordHasher` chuẩn của Microsoft.
- Email được trim, chuẩn hóa Unicode NFC và chữ thường; so sánh khi tra cứu theo collation hiện có của cột. Mật khẩu không bị trim, dài 12–128 ký tự.
- Trùng email dựa vào UNIQUE `uq_users_email` (kể cả đăng ký đồng thời) → 409.
- Khi `PasswordHasher` báo cần rehash, hash mới được ghi có điều kiện (chỉ nếu hash cũ chưa đổi).
- Register và login giới hạn 10 request/60 giây theo IP (`Auth:RateLimit:PermitLimit`, `Auth:RateLimit:WindowSeconds`), vượt giới hạn → 429. Sau reverse proxy cần cấu hình ForwardedHeaders để có IP thật.
- API trả 401/403 dạng ProblemDetails, không redirect. Policy `Customer`, `Bakery`, `Admin` đã khai báo; chưa có endpoint nghiệp vụ nào dùng.
- Lỗi database trả 500 ProblemDetails không có chi tiết; không bị coi là sai mật khẩu và không đăng xuất người dùng.

Giới hạn hiện tại:

- Chưa có xác minh email, quên/đổi mật khẩu.
- Không thu hồi từng phiên ở server: cookie đã cấp còn hợp lệ đến khi hết 2 giờ, trừ khi user bị `disabled`, bị xóa hoặc đổi role. Đăng xuất chỉ xóa cookie ở trình duyệt.
- Không có endpoint tạo tài khoản `bakery` hay `admin`.
- Khóa Data Protection (dùng để mã hóa cookie) đang theo mặc định của máy. Khi chạy nhiều instance hoặc container cần cấu hình nơi lưu khóa dùng chung.
- Mỗi request có cookie (trừ health) truy vấn bảng `users` một lần để kiểm tra trạng thái.

## Trạng thái xác minh — 15/10/2026

- SDK 10.0.401; restore và build thành công, 0 cảnh báo, 0 lỗi.
- `dotnet test` (không có MySQL kiểm thử): 112 đạt, 0 lỗi, 10 bỏ qua.
  - Fake/in-memory: 35 service, 55 API (TestServer HTTPS, Kestrel HTTPS thật, adapter hasher).
  - Chốt bảo vệ kiểm thử MySQL: 22 đạt (không cần MySQL).
  - MySQL thật: 10 ca bị bỏ qua khi không đặt `DECAKES_TEST_MYSQL_ADMIN` (xem `tests/README.md`).
- Luồng cookie trên Kestrel HTTPS thật với store trong bộ nhớ: đăng ký → đăng nhập → CSRF mới → `/me` → đăng xuất → `/me` 401. Client chỉ tin chứng chỉ phát triển làm root và vẫn kiểm tra hostname.
- Trên Aiven, chỉ chạy truy vấn đọc ở bước trước: SELECT theo email và theo id với khóa không tồn tại; kết nối `decakes_app` + `VerifyFull` xác minh trực tiếp từ secret.
- 16/10/2026: 10/10 ca `MySqlUserAccountStoreTests` đạt trên MySQL Community Server 8.4.11 (ZIP chính thức, chạy tạm trên `127.0.0.1`, cổng ngẫu nhiên, X Plugin tắt, không cài service). Đã kiểm chứng INSERT customer, đọc lại, trùng email và 12 lần đăng ký đồng thời (lỗi 1062), collation `utf8mb4_0900_ai_ci`, rehash có điều kiện và phân biệt hoa thường, UTC, tài khoản không có quyền DELETE. Persistence chưa được chạy trên Aiven (cố ý).