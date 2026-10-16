# Kiểm thử

Chạy: `dotnet test` tại thư mục repository.

| Project | Loại | Phạm vi |
| --- | --- | --- |
| `DeCakes.Application.Tests` | Fake | Accounts, paging và Catalog services: validation, defaults, filter, lookahead, 64-bit offset, detail outcomes, lỗi và cancellation. |
| `DeCakes.Api.Tests` | Fake/in-memory | API qua `WebApplicationFactory` (TestServer scheme HTTPS) và Kestrel HTTPS thật, store user trong bộ nhớ, `PasswordHasher` thật: CSRF, cookie flags và hạn 2 giờ, `/me`, logout, user bị disabled/xóa/đổi role, 401/403, 429, lỗi database được che, health không phụ thuộc database, nhánh hash giả. |
| `DeCakes.Infrastructure.Tests` | Chốt bảo vệ + MySQL thật | `SafetyGuardTests` chạy luôn. `MySqlUserAccountStoreTests`, `MySqlBakeryCatalogStoreTests`, `MySqlCakeCatalogStoreTests` chạy store thật trên MySQL 8.4 riêng; **bị bỏ qua** nếu chưa đặt `DECAKES_TEST_MYSQL_ADMIN`. |

Kiểm thử API dùng môi trường `Testing` (không nạp user-secrets), Data Protection khóa tạm, `FakeTimeProvider`. Ca Kestrel HTTPS cần chứng chỉ phát triển (`dotnet dev-certs https`); client chỉ tin chứng chỉ đó làm root, vẫn kiểm tra hostname, không tắt xác minh. Thiếu chứng chỉ thì ca này bị bỏ qua.

## Kiểm thử MySQL thật (persistence)

Không dùng Aiven, secret development hay dữ liệu demo. Kiểm thử cần một **MySQL 8.4 chỉ dùng để kiểm thử**, chạy trên loopback.

Chốt bảo vệ (`MySqlTestSettings`):

- Chỉ đọc biến `DECAKES_TEST_MYSQL_ADMIN`; không đọc `ConnectionStrings:DeCakesDb` hay user-secrets.
- Từ chối host không phải `localhost`/`127.0.0.1`/`::1`, chuỗi có `aivencloud`, tài khoản `avnadmin`/`decakes_app`, chuỗi có sẵn `Database`.
- Từ chối server không phải 8.4.x.
- Tự tạo database `decakes_test_<ngẫu nhiên>` và tài khoản `dc_test_<ngẫu nhiên>` (SELECT toàn database tạm; INSERT, UPDATE trên `users`; không DELETE), rồi xóa cả hai khi xong.
- Nạp `database/install/01_schema.sql` gốc sau khi bỏ đúng dòng `USE decakes_db;`. Script bị từ chối nếu còn `USE`, `CREATE/DROP/ALTER DATABASE|SCHEMA`, `GRANT` hay `CREATE USER`. Trước khi nạp, kiểm tra `SELECT DATABASE()` là database tạm.

Ví dụ với Docker (cổng 3307 chỉ trên loopback, mật khẩu tạm chỉ cho container này):

```
docker run --rm -d --name decakes-mysql-test -p 127.0.0.1:3307:3306 -e MYSQL_ROOT_PASSWORD=<mật khẩu tạm> mysql:8.4
```

Hoặc bản ZIP MySQL 8.4 (Windows, không cài đặt) chạy `mysqld --initialize-insecure` trên thư mục dữ liệu riêng và `--port=3307 --bind-address=127.0.0.1`. Không dùng MySQL đang phục vụ việc khác trên máy.

Sau đó trong cùng terminal:

```
$env:DECAKES_TEST_MYSQL_ADMIN = "Server=127.0.0.1;Port=3307;User Id=root;Password=<mật khẩu tạm>"
dotnet test tests/DeCakes.Infrastructure.Tests
```

Các ca: INSERT customer (role, status, is_demo, phone NULL, UTC), đọc lại theo id và email; trùng email và 12 lần đăng ký đồng thời (một bản ghi, lỗi 1062 → `DuplicateEmailException`); so sánh email theo collation `utf8mb4_0900_ai_ci` (hoa/thường và dấu); rehash khi hash cũ khớp, không ghi đè khi khác, so sánh hash phân biệt hoa thường; tài khoản kiểm thử không có quyền DELETE.

## Kiểm thử Catalog

- API tests dùng store trong bộ nhớ: bốn GET, public DTO fields, JSON null/decimal, paging/filter, 200/400/404/500, cookie/session regression. Không dùng kết quả fake để khẳng định SQL đúng.
- Persistence tests dùng database tạm riêng cho từng test. Helper setup chỉ INSERT bakery/cake sau khi kiểm tra tên database và `SELECT DATABASE()`; store dùng restricted factory, không tăng quyền ghi Catalog cho test account.
- MySQL cases kiểm tra visibility/demo ở cả cake và bakery cha, cùng predicate cho list/detail, filter trước paging, giá null/0/DECIMAL(12,2), Unicode, contact nullable và offset 64-bit. Không chạy các ca này trên Aiven.
- Để chạy toàn bộ database-free regression, tạm bỏ biến `DECAKES_TEST_MYSQL_ADMIN` khỏi process rồi khôi phục sau (xem [workflow](../docs/DEVELOPMENT_WORKFLOW.md#validation-commands)), dùng filter:

```powershell
dotnet test DeCakes.slnx --filter 'FullyQualifiedName!~MySqlUserAccountStoreTests&FullyQualifiedName!~MySqlBakeryCatalogStoreTests&FullyQualifiedName!~MySqlCakeCatalogStoreTests'
```

- Khi đã cấu hình MySQL disposable loopback theo mục trên, kiểm thử persistence Catalog và regression Accounts:

```powershell
dotnet test tests/DeCakes.Infrastructure.Tests --filter 'FullyQualifiedName~MySqlBakeryCatalogStoreTests|FullyQualifiedName~MySqlCakeCatalogStoreTests|FullyQualifiedName~MySqlUserAccountStoreTests'
```

## Dự kiến bổ sung

- `DeCakes.Domain.Tests`: quy tắc nghiệp vụ khi có.
- Kiểm thử gọi stored procedure, OUT parameter và SQLSTATE 45000 trên MySQL kiểm thử riêng (cần nạp `02_procedures.sql` qua bước rà tương tự).
