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
  DeCakes.Domain/          Khái niệm và quy tắc nghiệp vụ, không phụ thuộc HTTP/EF Core/MySQL
  DeCakes.Application/     DTO, interface, service nghiệp vụ, theo chức năng
  DeCakes.Infrastructure/  DbContext, model scaffold, truy cập dữ liệu, triển khai interface
  DeCakes.Api/             ASP.NET Core Web API (Controllers), cấu hình, xử lý lỗi HTTP
tests/                     Kế hoạch kiểm thử (xem tests/README.md)
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
- Nghiệp vụ gọi các stored procedure đã có. Procedure tự quản lý transaction phải gọi ngoài transaction của EF Core.
- Ứng dụng không chạy migration, `EnsureCreated` hay thay đổi database khi khởi động.
- Provider: `MySql.EntityFrameworkCore` 10.x (Oracle). Pomelo chưa có bản stable cho EF Core 10 tại thời điểm tạo khung.
- Package đã có trong Infrastructure; chưa scaffold model, đăng ký DbContext hoặc kết nối Aiven. Cần kiểm chứng provider với schema và procedure thực tế trước khi triển khai nghiệp vụ.

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

Kiểm tra: `GET http://localhost:5011/api/health` trả HTTP 200. Endpoint này chưa kiểm tra database. Ở môi trường Development, OpenAPI tại `/openapi/v1.json`.

Profile mặc định `http` dùng cổng 5011. Để chạy HTTPS, dùng `dotnet run --project src/DeCakes.Api --launch-profile https` (cổng 7215, cần chứng chỉ phát triển trên máy).

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

Tự gõ trực tiếp trên máy, không dán vào chat hay file trong repository. Dạng chuỗi (placeholder):

```
Server=<host>;Port=<port>;Database=decakes_db;User Id=<tài khoản backend>;Password=<mật khẩu>;SslMode=VerifyFull;SslCa=<đường dẫn tới ca.pem>
```

- Lấy host, port và CA certificate trong Aiven Console. Lưu `ca.pem` ngoài repository hoặc trong `certs/` (đã bị `.gitignore` loại trừ).
- Không hạ `SslMode` xuống `Preferred`/`Required` hay tắt kiểm tra chứng chỉ để vượt lỗi kết nối.
- Tài liệu Connector/NET ghi `VerifyFull` với PEM cần `SslCa`, và nhắc cả `SslCert`/`SslKey`. Aiven không yêu cầu chứng chỉ client, nên cần thử xem provider có chấp nhận `VerifyFull` chỉ với `SslCa` không. Chưa xác minh.
- Tài khoản phải là user MySQL riêng cho backend (xem `database/security/app_user_grants.sql`), không phải `avnadmin`.

### Scaffold model (chỉ đọc metadata)

Sau khi đã đặt chuỗi kết nối:

```
dotnet tool restore
./scripts/scaffold-db.ps1
```

Script từ chối chạy nếu thiếu cấu hình hoặc đã có model (trừ khi dùng `-Force`), và không in chuỗi kết nối. Tooling: `dotnet-ef` 10.0.9 (local tool, `dotnet-tools.json`) và `Microsoft.EntityFrameworkCore.Design` 10.0.9 trong Api (`PrivateAssets=all`).

## Trạng thái xác minh khung — 15/10/2026

- SDK 10.0.401; restore và build 4 project thành công, 0 cảnh báo, 0 lỗi.
- Chạy API ở Development: `GET /api/health` trả HTTP 200 với `status = Healthy`, `service = DeCakes.Api` và thời gian UTC.
- `GET /openapi/v1.json` trả HTTP 200 và có operation `GET /api/health`.
- Chạy ở Production: health trả HTTP 200, OpenAPI trả HTTP 404.
- Chưa kết nối database, chưa chạy lại SQL hay kiểm thử nghiệp vụ trên Aiven.

### Trạng thái kết nối database — 15/10/2026

- Chưa có `ConnectionStrings:DeCakesDb` trên máy (không có user-secrets, không có biến môi trường), nên **chưa scaffold, chưa đăng ký DbContext, chưa kiểm tra kết nối**. Đã chuẩn bị tooling và script scaffold (xem trên).

Khung đã chạy được. Bước tiếp theo là cấu hình kết nối hạn chế quyền và kiểm chứng model/provider theo [kế hoạch database](docs/BACKEND_DATABASE_PLAN.md), sau đó triển khai Accounts và luồng yêu cầu → báo giá → đơn.
