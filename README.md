# DeCakes Backend

Backend cho website đặt bánh theo yêu cầu: khách gửi một yêu cầu đến nhiều tiệm, nhận báo giá, chọn một báo giá và chờ tiệm xác nhận đơn. C#/.NET 10, Clean Architecture tinh gọn.

**Triển khai Aiven — 31/10/2026:** đã áp dụng V003 → V004 → V005 và grants cho cả hai tài khoản backend. Database có 17 bảng, 12 procedure; dữ liệu cũ giữ nguyên. Đã tạo gói `basic` / **Cơ bản**, giá tháng/năm để NULL. Hai tiệm active cũ chưa có kỳ sử dụng nên bị chặn giao dịch mới. Email Resend chưa đủ cấu hình, payOS đang tắt; chưa nghiệm thu toàn bộ luồng ghi trên Aiven. Xem [báo cáo triển khai và giới hạn](docs/AIVEN_DEPLOYMENT_2026-10-31.md). Không chạy lại các migration đã áp dụng.

**Admin thật đã xác minh:** tạo thành công ngày 31/10/2026 bằng mật khẩu nhập ẩn; đăng nhập, `/api/auth/me` và dashboard Admin đã chạy trên Aiven. Công cụ đăng xuất sau khi kiểm tra; người dùng đăng nhập lại bằng tài khoản vừa tạo để sử dụng.

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

MediatR, CQRS, Generic Repository, Unit of Work, microservices, AI tạo ảnh, thanh toán tiền bánh, chat, giao hàng. (Có thu phí gói sử dụng của tiệm, xem mục Gói.)

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

Chạy các lệnh trên tại thư mục gốc của checkout có file `DeCakes.slnx`; mở chính thư mục đó trong VS Code. Đường dẫn tùy máy, không phụ thuộc `D:\DeCakes\BE`.

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

Kiểm thử: `dotnet test`. Application/API dùng fake hoặc store trong bộ nhớ; kiểm thử persistence MySQL thật chỉ được bật khi có `DECAKES_TEST_MYSQL_ADMIN` và sẽ tạo/xóa database cùng user tạm trên MySQL kiểm thử riêng. Để chạy kiểm tra không kết nối database, dùng [lệnh có lọc và cô lập biến môi trường](docs/DEVELOPMENT_WORKFLOW.md#validation-commands). Xem [tests/README.md](tests/README.md) trước khi bật kiểm thử MySQL; không dùng Aiven.

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
- Cấu hình email Resend: `dotnet run scripts/SetEmailSecret.cs` (API key nhập ẩn, xem mục Cấu hình email).

## Xác thực (Accounts)

Dùng bảng `users` hiện có, không thêm bảng Identity. Mật khẩu hash bằng `PasswordHasher<TUser>` của Microsoft (V3, PBKDF2-HMAC-SHA512). Đăng nhập bằng cookie, không JWT hay refresh token. Giả định frontend và API cùng origin; khi phát triển có thể dùng proxy của frontend. Không bật CORS.

| Endpoint | Mô tả | CSRF | Rate limit |
| --- | --- | --- | --- |
| `GET /api/auth/csrf` | Cấp CSRF token, `Cache-Control: no-store` | | |
| `POST /api/auth/register` | `{ email, password, fullName, phone? }` → 201 với user an toàn (`emailVerified: false`). Chỉ tạo `customer`, `active`, `is_demo = 0`. Gửi email xác minh; gửi lỗi không hủy đăng ký. Không tự đăng nhập | Bắt buộc | Có |
| `POST /api/auth/login` | `{ email, password }` → 200 và cookie đăng nhập. Email chưa xác minh vẫn đăng nhập được | Bắt buộc | Có |
| `POST /api/auth/logout` | Xóa cookie đăng nhập của trình duyệt này → 204 | Bắt buộc | |
| `GET /api/auth/me` | User hiện tại `{ id, email, fullName, phone, role, emailVerified }`; 401 nếu chưa đăng nhập | | |
| `POST /api/auth/email-verification/resend` | Đã đăng nhập. 202; 409 `EMAIL_ALREADY_VERIFIED`; 429 `EMAIL_RESEND_THROTTLED`; 503 `EMAIL_DELIVERY_UNAVAILABLE` | Bắt buộc | Khôi phục |
| `POST /api/auth/email-verification/confirm` | `{ token }` → 204; mọi token không dùng được → 400 `TOKEN_INVALID` | Bắt buộc | Khôi phục |
| `POST /api/auth/password/forgot` | `{ email }` → luôn 202 không nội dung (email sai định dạng → 400) | Bắt buộc | Khôi phục |
| `POST /api/auth/password/reset` | `{ token, newPassword }` → 204; mật khẩu sai quy tắc → 400 validation (token không bị tiêu thụ); token không dùng được → 400 `TOKEN_INVALID` | Bắt buộc | Khôi phục |
| `POST /api/auth/password/change` | Đã đăng nhập. `{ currentPassword, newPassword }` → 204 + cookie mới; 400 `CURRENT_PASSWORD_INVALID`; 409 `PASSWORD_CHANGED_CONCURRENTLY` | Bắt buộc | Có |

### Xác minh email, khôi phục mật khẩu và thu hồi phiên

Cần [V003](database/migrations/V003__account_security.sql) và [grants](database/migrations/V003__account_security.grants.sql) (`users.email_verified_at`, `users.session_version`, bảng `user_tokens`). **Đã áp dụng lên Aiven ngày 31/10/2026**; không chạy lại. Database khác chưa có V003 sẽ không chạy được phần mã này.

- Token: 32 byte CSPRNG, gửi dạng base64url trong link `{App:PublicBaseUrl}/verify-email#token=…` hoặc `/reset-password#token=…` (fragment: không vào log server/Referer). Frontend đọc fragment rồi POST. Không có GET nào thay đổi dữ liệu. Database chỉ lưu SHA-256; token gắn mục đích, email lúc cấp, hạn (xác minh 24 giờ, đặt lại 30 phút), dùng một lần. Cấp token mới thu hồi token chưa dùng cùng mục đích. Token không ghi vào log.
- Tiêu thụ token, đổi hash, tăng `session_version` và thu hồi token đặt lại khác nằm trong một transaction có khóa dòng (`user_tokens` rồi `users`): hai lần dùng đồng thời chỉ một lần thành công.
- Token mất hiệu lực khi user đổi email, bị `disabled`, hết hạn, bị thu hồi, sai mục đích. Đặt lại mật khẩu không mở lại user `disabled` và không áp dụng cho tài khoản demo. Đặt lại thành công đánh dấu email đã xác minh (đã chứng minh hộp thư).
- Giới hạn: theo IP cho nhóm khôi phục (`Auth:RecoveryRateLimit`, mặc định 5 request/300 giây) và theo user+mục đích (cách nhau ≥ 60 giây, ≤ 5 token/giờ). Quên mật khẩu vượt ngưỡng vẫn trả 202.
- Thu hồi phiên: cookie mang claim `decakes:sv` = `users.session_version` lúc cấp; mỗi request so với database. Đổi/đặt lại mật khẩu làm mọi cookie đã cấp mất hiệu lực ở mọi trình duyệt; đổi mật khẩu cấp lại cookie cho trình duyệt đang dùng. Cookie cấp trước khi triển khai (không có claim) bị từ chối một lần.
- Mức truy cập khi chưa xác minh: đăng nhập, `/me`, đổi mật khẩu, gửi lại email, đọc dữ liệu của mình. Bị chặn (403 `EMAIL_NOT_VERIFIED`): customer tạo/gửi yêu cầu; owner gửi hồ sơ duyệt, mời staff và checkout. Chọn báo giá tạo đơn chưa có endpoint và phải gắn `[RequireVerifiedEmail]` khi xây.
- Tài khoản cũ: giữ `email_verified_at = NULL` (không tự đánh dấu), vẫn đăng nhập và tự xác minh. Admin (kể cả tạo bằng bootstrap) không bị chặn ở API Admin hiện có; **đề xuất** khi có email thật: admin tự xác minh qua cùng luồng, sau đó mới bật yêu cầu xác minh cho thao tác ghi của Admin.
- Gửi email: `ResendAccountEmailSender` gọi `POST https://api.resend.com/emails` bằng `HttpClient`, chỉ khi có `App:PublicBaseUrl` (HTTPS), `Email:From`, `Email:Resend:ApiKey` (đặt bằng user-secrets/biến môi trường). Thiếu cấu hình: không gọi mạng, coi như gửi thất bại; ứng dụng và health vẫn chạy. Request có header `User-Agent` (Resend trả 403 nếu thiếu; trước 01/11/2026 adapter không gửi header này). Log chỉ gồm loại email, id email Resend trả về (`Resend đã nhận email …, id …`), mã HTTP + tên lỗi Resend, tên khóa cấu hình còn thiếu; không có địa chỉ email, token, link hay khóa. **Chưa kiểm chứng với tài khoản/domain Resend thật** — xem mục Cấu hình email bên dưới.
- Rủi ro còn lại: thời gian phản hồi của quên mật khẩu khác nhau giữa email có và không có tài khoản (ghi DB + gọi dịch vụ email đồng bộ); cần hàng đợi gửi nền nếu muốn che hoàn toàn. Đổi mật khẩu và đặt lại mật khẩu đồng thời trên cùng user có thể bị InnoDB phát hiện deadlock và rollback một bên (500, không ghi dở).

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
- API trả 401/403 dạng ProblemDetails, không redirect. Policy `Customer` dùng cho DesignRequests, `Bakery` cho inbox của tiệm, `Admin` cho API quản trị chỉ đọc.
- Lỗi database trả 500 ProblemDetails không có chi tiết; không bị coi là sai mật khẩu và không đăng xuất người dùng.

Giới hạn hiện tại:

- Không thu hồi từng phiên riêng lẻ ở server: cookie đã cấp còn hợp lệ đến khi hết 2 giờ, trừ khi user bị `disabled`, bị xóa, đổi role hoặc đổi/đặt lại mật khẩu. Đăng xuất chỉ xóa cookie ở trình duyệt đó.
- Không có endpoint tạo tài khoản `admin`. Tài khoản `bakery` được tạo qua đăng ký chủ tiệm hoặc nhận lời mời (mục Onboarding).
- Khóa Data Protection (dùng để mã hóa cookie) đang theo mặc định của máy. Khi chạy nhiều instance hoặc container cần cấu hình nơi lưu khóa dùng chung.
- Mỗi request có cookie (trừ health) truy vấn bảng `users` một lần để kiểm tra trạng thái và session version.

### Cấu hình email (Resend)

1. Cần tài khoản Resend và một trong hai: domain đã xác minh trong Resend (bản ghi DKIM/SPF lấy ở tab Records của domain trong dashboard, nên dùng subdomain như `send.ten-mien.vn`), hoặc chỉ để thử: `onboarding@resend.dev`, **chỉ gửi được tới email đăng ký tài khoản Resend**. Không dùng `resend.dev` cho người dùng thật.
2. Giữ **tắt** Click tracking/Open tracking của domain (mặc định tắt). Bật click tracking làm Resend viết lại link qua máy chủ theo dõi, link mang token sẽ đi qua bên thứ ba.
3. Chạy `dotnet run scripts/SetEmailSecret.cs` tại thư mục repository: nhập `App:PublicBaseUrl`, `Email:From`, API key (nhập ẩn). Script ghi ba khóa vào user-secrets, giữ nguyên secret khác (chuỗi kết nối), không in khóa, từ chối input qua pipe. Khởi động lại API sau khi ghi.
4. **Chưa có frontend nhận link** `/verify-email#token=…`, `/reset-password#token=…`, `/accept-invitation#token=…`. Khi nghiệm thu có thể đặt `App:PublicBaseUrl=https://localhost:7215`: mở link sẽ thấy 404 của API, người thử chép phần sau `#token=` (trên thanh địa chỉ hoặc trong nội dung email) vào Postman rồi gọi POST. Đây không phải trải nghiệm người dùng cuối.
5. Nghiệm thu bằng collection riêng: [tools/postman/README.md](tools/postman/README.md#10-nghiệm-thu-email-tài-khoản-collection-riêng).

## Admin giai đoạn 1

Năm GET dùng cookie và policy `Admin` hiện có: `/api/admin/dashboard`, `/api/admin/users`, `/api/admin/users/{id}`, `/api/admin/bakeries`, `/api/admin/bakeries/{id}`. Anonymous nhận 401, customer/bakery nhận 403; admin bị disabled hoặc đổi role mất phiên ở request kế tiếp.

Danh sách trả `{ items, page, pageSize, totalCount }`, mặc định trang 1/cỡ 20, tối đa 100, thứ tự `id ASC`. Users lọc `search` (email/họ tên), `role`, `status`, `isDemo`; bakeries lọc `search` (tên/slug), `profileStatus`, `isDemo`. Tìm chuỗi con literal, không coi `%`/`_` là wildcard. Bỏ `isDemo` gồm cả demo và thật, không áp điều kiện public của Catalog. Query/id sai trả 400; detail không có trả 404. Không trả hoặc SELECT hash trong truy vấn Admin đọc user.

Dashboard đếm user theo role/status, tiệm theo profileStatus, yêu cầu và đơn theo status; mọi nhóm hợp lệ đều có, kể cả 0. `isDemo` áp vào cờ của từng bảng; đơn lấy cờ từ yêu cầu nguồn vì orders không có cột này. Không có số liệu doanh thu. GET không thay đổi dữ liệu; response thành công có `Cache-Control: no-store`.

Công cụ console tạo admin mới nằm ở `tools/DeCakes.AdminBootstrap`, không đăng ký vào HTTP và không tự chạy khi API khởi động. Nhập mật khẩu ẩn hai lần; chỉ INSERT `admin`, `active`, `is_demo=0`, dừng khi email trùng. Xem [hợp đồng, hướng dẫn bootstrap và kiểm chứng](docs/ADMIN_PHASE1.md). Duyệt tiệm đã có trong mục Onboarding bên dưới. Chưa có thao tác admin khóa user, đổi role, đặt lại mật khẩu của người khác hoặc quản lý đơn.

## Catalog: xem tiệm và mẫu bánh

Các endpoint GET công khai, không yêu cầu đăng nhập hoặc CSRF token. Nếu request có cookie, middleware vẫn kiểm tra phiên theo quy tắc Accounts hiện có.

| Endpoint | Kết quả |
| --- | --- |
| `GET /api/bakeries` | Danh sách tiệm `active`, `is_demo = 0`; item gồm `id`, `name`, `slug`, `city`. |
| `GET /api/bakeries/{id}` | Chi tiết cùng điều kiện public; thêm `description`, `addressLine`, `phone`. |
| `GET /api/cakes` | Mẫu bánh `published`, không demo, thuộc tiệm active không demo; item gồm `id`, `bakeryId`, `name`, `referencePrice`, `currency`. Có filter `bakeryId` tùy chọn. |
| `GET /api/cakes/{id}` | Chi tiết cùng điều kiện public; thêm `description`, `flavor`, `sizeLabel`. |

- List nhận `page` mặc định 1 và `pageSize` mặc định 20 (1–100), sắp xếp `id ASC`; response có `items`, `page`, `pageSize`, `hasNext`. Không trả tổng số bản ghi hoặc ảnh trong MVP.
- Paging thiếu/rỗng dùng default; `bakeryId` thiếu/rỗng nghĩa là không lọc. Query key khác bị bỏ qua. Input sai kiểu, tràn số, page/ID không dương hoặc size ngoài phạm vi → 400 ValidationProblemDetails.
- ID là số nguyên long dương. Detail không tồn tại hoặc không public đều trả 404 ProblemDetails. List rỗng/vượt trang cuối hoặc filter bakery không tồn tại/không public trả 200 với `items: []`.
- Giữ null của cột nullable; giá dùng decimal/JSON number hoặc null, không thay null bằng 0. Lỗi hệ thống trả masked 500 theo cấu hình hiện có.
- Chạy API bằng profile HTTPS rồi thử `https://localhost:7215/api/bakeries?page=1&pageSize=2` hoặc `https://localhost:7215/api/cakes?page=1&pageSize=2`. Không seed/sửa Aiven để tạo dữ liệu thử. Xem [tests/README.md](tests/README.md) cho kiểm thử local.

## Yêu cầu đặt bánh (DesignRequests)

Phần của khách: tạo và sửa yêu cầu, gửi đến từng tiệm, xem tiệm đã nhận. Mọi endpoint dùng policy `Customer`; customer id lấy từ claim của cookie, không lấy từ body. Thao tác ghi cần header `X-CSRF-TOKEN` như Accounts.

| Endpoint | Mô tả | Thành công |
| --- | --- | --- |
| `POST /api/design-requests` | Tạo yêu cầu `draft`, tiền tệ `VND`, `is_demo = 0` | 201, header `Location`, chi tiết yêu cầu |
| `GET /api/design-requests?status=&page=&pageSize=` | Yêu cầu của chính khách, sắp xếp `created_at DESC, id DESC`. `page ≥ 1`, `pageSize` mặc định 20, tối đa 100 | 200 `{ items, page, pageSize, totalCount }` |
| `GET /api/design-requests/{id}` | Chi tiết yêu cầu của chính khách | 200 |
| `PUT /api/design-requests/{id}` | Thay toàn bộ nội dung, chỉ khi còn `draft` | 200 |
| `POST /api/design-requests/{id}/bakeries/{bakeryId}` | Gửi đến **một** tiệm qua `sp_send_request_to_bakery`. Lần gửi đầu chuyển `draft` → `open` | 201, `Location` tới danh sách tiệm, thông tin tiệm vừa nhận |
| `GET /api/design-requests/{id}/bakeries` | Các tiệm đã nhận, trạng thái (`pending`, `viewed`, `quoted`, `declined`), lý do từ chối nếu có | 200 |

Ví dụ tạo yêu cầu (sau khi đăng nhập và lấy CSRF token):

```http
POST /api/design-requests
Content-Type: application/json
X-CSRF-TOKEN: <token>

{
  "title": "Bánh sinh nhật 2 tầng",
  "ideaDescription": "Tông hồng pastel, hoa kem bơ.",
  "budgetMin": 500000,
  "budgetMax": 1200000.50,
  "sizeDescription": "20 cm",
  "servings": 12,
  "flavor": "Vani",
  "inscriptionText": "Mừng sinh nhật",
  "pickupAt": "2026-10-20T09:30:00+07:00",
  "inspiredByCakeId": null
}
```

Gửi đến tiệm 9: `POST /api/design-requests/15/bakeries/9` (không có body, có `X-CSRF-TOKEN`). Mỗi lần gọi gửi một tiệm và là một transaction riêng; gửi nhiều tiệm là nhiều lần gọi, có thể thành công một phần.

Kiểm tra dữ liệu (theo `design_requests` trong `01_schema.sql`):

- `title` bắt buộc, tối đa 200 ký tự. `ideaDescription` bắt buộc, tối đa 65 535 byte UTF-8 (cột `TEXT`). `sizeDescription` ≤ 100, `flavor` ≤ 200, `inscriptionText` ≤ 200 ký tự, tùy chọn. Chuỗi được trim và chuẩn hóa NFC; trường một dòng không nhận ký tự điều khiển.
- `budgetMax` > 0; `budgetMin` tùy chọn, ≥ 0 và ≤ `budgetMax`. Tối đa 9 999 999 999.99 và nhiều nhất 2 chữ số thập phân (`DECIMAL(12,2)`); giá trị không hợp lệ bị từ chối, không làm tròn.
- `servings` tùy chọn, 1–65 535 (`SMALLINT UNSIGNED`). Kích thước và số phần đều không bắt buộc.
- `pickupAt` bắt buộc, ISO 8601 có `Z` hoặc offset, chính xác tối đa mili giây (`DATETIME(3)`), phải ở tương lai theo `TimeProvider`. Lưu và trả về theo UTC (`...Z`).
- `inspiredByCakeId` tùy chọn; phải là mẫu bánh `published` của tiệm `active`.

**Quy tắc sửa draft (quyết định ở tầng ứng dụng):** chỉ sửa được khi yêu cầu còn `draft`. Chú thích trong `01_schema.sql` cho phép sửa cả `draft`/`open`; ứng dụng chặt hơn để mọi tiệm báo giá trên cùng một nội dung. SQL nguồn không bị sửa. Kiểm tra `draft` và ghi nằm trong cùng một câu `UPDATE ... WHERE status = 'draft'`, nên không thể sửa sau khi lần gửi tiệm đồng thời đã chuyển yêu cầu sang `open`. Sửa với dữ liệu không đổi vẫn trả 200.

Ánh xạ lỗi (ProblemDetails có thêm `code`):

| Tình huống | HTTP | `code` |
| --- | --- | --- |
| Dữ liệu không hợp lệ | 400 | (ValidationProblemDetails, theo trường) |
| Procedure: `INVALID_ARGUMENT` | 400 | `INVALID_ARGUMENT` |
| Yêu cầu không tồn tại hoặc của khách khác (kể cả `NOT_REQUEST_OWNER`) | 404 | `REQUEST_NOT_FOUND` |
| `BAKERY_NOT_FOUND` | 404 | `BAKERY_NOT_FOUND` |
| Sửa khi không còn draft | 409 | `REQUEST_NOT_DRAFT` |
| `REQUEST_ALREADY_SENT` | 409 | `REQUEST_ALREADY_SENT` |
| `REQUEST_NOT_OPEN` (yêu cầu đã `ordered`/`cancelled`) | 409 | `REQUEST_NOT_OPEN` |
| `REQUEST_PICKUP_IN_PAST` | 409 | `REQUEST_PICKUP_IN_PAST` |
| `BAKERY_NOT_ACTIVE` (tiệm không nhận yêu cầu) | 409 | `BAKERY_NOT_ACTIVE` |
| `USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `USER_NOT_CUSTOMER` | 403 | `ACCOUNT_NOT_ALLOWED` |
| Tạo yêu cầu hoặc gửi đến tiệm khi email chưa xác minh | 403 | `EMAIL_NOT_VERIFIED` |
| SIGNAL ngoài allowlist hoặc lỗi database khác | 500 | (không chi tiết) |

Mã SIGNAL được nhận diện khi `SqlState = 45000` và `Message` trùng đúng mã trong allowlist (`SendRequestProcedureErrors`); trên MySQL 8.4.11 đã kiểm chứng `Number = 1644`. Kiểm thử đối chiếu allowlist với mọi `MESSAGE_TEXT` của `sp_send_request_to_bakery`, `sp_guard_active_customer` và `sp_guard_bakery_active`. Lỗi MySQL không bao giờ được trả nguyên văn cho client. Gửi tiệm không retry tự động: nếu kết nối đứt khi gọi procedure, không biết đã commit hay chưa; client xem lại bằng `GET .../bakeries`.

Giới hạn hiện tại:

- Chưa có ảnh tham khảo (upload, `media_assets`), hủy yêu cầu, AI tạo ảnh.
- Tiệm đã đọc được yêu cầu nhận được (mục dưới), nhưng chưa đánh dấu đã xem/từ chối được; đã có đọc lịch sử báo giá, chưa có API ghi báo giá hoặc đơn.
- Khách chọn tiệm từ Catalog (`GET /api/bakeries`); endpoint gửi tiệm nhận `bakeryId` đã biết.
- Mẫu bánh truyền cảm hứng được kiểm tra lúc tạo/sửa; nếu tiệm ẩn mẫu sau đó, yêu cầu vẫn giữ liên kết (khóa ngoại chỉ bảo đảm mẫu tồn tại).

## Inbox của tiệm (BakeryWorkspace)

Phần của thành viên tiệm, **chỉ đọc**: tiệm của tôi và các yêu cầu khách đã gửi đến tiệm. Không phải Catalog công khai. Mọi endpoint dùng policy `Bakery`; user id lấy từ claim của cookie.

| Endpoint | Mô tả | Thành công |
| --- | --- | --- |
| `GET /api/bakery-workspace/bakeries` | Các tiệm mà user là thành viên `active` | 200 `[{ bakeryId, name, profileStatus, memberRole }]` |
| `GET /api/bakery-workspace/bakeries/{bakeryId}/requests?status=&requestStatus=&page=&pageSize=` | Yêu cầu đã gửi đến tiệm (`request_bakeries`), sắp xếp `sent_at DESC, request_bakeries.id DESC`. `status` lọc trạng thái lần gửi (`pending`, `viewed`, `quoted`, `declined`), `requestStatus` lọc trạng thái yêu cầu (`draft`, `open`, `ordered`, `cancelled`). `page ≥ 1`, `pageSize` mặc định 20, tối đa 100 | 200 `{ items, page, pageSize, totalCount }` |
| `GET /api/bakery-workspace/bakeries/{bakeryId}/requests/{requestId}` | Nội dung yêu cầu và trạng thái lần gửi đến tiệm | 200 |

Ví dụ: `GET /api/bakery-workspace/bakeries/9/requests?status=pending&pageSize=10`

```json
{
  "items": [
    {
      "requestId": 15,
      "title": "Bánh sinh nhật 2 tầng",
      "budgetMin": 500000.00,
      "budgetMax": 1200000.50,
      "currency": "VND",
      "pickupAt": "2026-10-20T02:30:00Z",
      "requestStatus": "open",
      "status": "pending",
      "sentAt": "2026-10-16T03:12:45.120Z"
    }
  ],
  "page": 1,
  "pageSize": 10,
  "totalCount": 1
}
```

Chi tiết `GET /api/bakery-workspace/bakeries/9/requests/15` trả thêm `ideaDescription`, `sizeDescription`, `servings`, `flavor`, `inscriptionText`, `inspiredByCakeId`, `declineReason`, `respondedAt`. Không có email, số điện thoại, tên, `password_hash` hay id tài khoản khách, và chưa có ảnh vì upload chưa triển khai.

Quyền đọc:

- Được đọc khi user `active`, có role `bakery` và là thành viên `active` (owner hoặc staff) của tiệm, giống `sp_guard_active_bakery_member`. Thành viên `removed` không đọc được.
- Điều kiện thành viên nằm ngay trong từng truy vấn đọc (`EXISTS` trên `bakery_members` và `users`), không chỉ kiểm tra một lần ở controller. `bakeryId` trên route chỉ là tham số tra cứu, không phải bằng chứng có quyền.
- Tiệm `draft`/`pending_review`/`suspended` vẫn được thành viên active xem lịch sử. Quyền nhận yêu cầu hay gửi báo giá sẽ được kiểm tra riêng ở thao tác ghi (procedure đòi tiệm `active`).
- GET không đổi `pending` → `viewed` và không cập nhật thời gian nào.

Ánh xạ lỗi (ProblemDetails có thêm `code`):

| Tình huống | HTTP | `code` |
| --- | --- | --- |
| `status`, `requestStatus`, `page`, `pageSize` không hợp lệ | 400 | (ValidationProblemDetails, theo tham số) |
| Tiệm không tồn tại hoặc user không phải thành viên active | 404 | `BAKERY_NOT_FOUND` |
| Yêu cầu không tồn tại hoặc chưa gửi đến tiệm này | 404 | `REQUEST_NOT_FOUND` |
| Chưa đăng nhập / không phải role `bakery` | 401 / 403 | |
| Lỗi database | 500 | (không chi tiết) |

Giới hạn hiện tại:

- Đăng ký tiệm, hồ sơ, thành viên: xem mục Onboarding bên dưới.
- Chưa có đánh dấu đã xem, từ chối yêu cầu hoặc API ghi báo giá.

## Đọc báo giá

Customer xem báo giá của yêu cầu mình tại `GET /api/design-requests/{requestId}/quotes[/{quoteId}]`. Thành viên tiệm xem báo giá riêng của tiệm tại `GET /api/bakery-workspace/bakeries/{bakeryId}/requests/{requestId}/quotes[/{quoteId}]`. Mỗi phiên bản trả đầy đủ nội dung, `isCurrent` và `isExpired`; không lộ thông tin tài khoản khách/người tạo. Danh sách gồm cả lịch sử, phân trang mặc định 20/tối đa 100, lọc `status`, sắp xếp `created_at DESC, id DESC`. GET không ghi database. Chi tiết quyền, lỗi, kiểm chứng và quy tắc đã chốt cho phần ghi tiếp theo: [docs/QUOTES.md](docs/QUOTES.md).
- Chưa có ảnh tham khảo của yêu cầu.

## Onboarding tiệm, staff và admin review

Cần [V004](database/migrations/V004__bakery_onboarding.sql) + [grants](database/migrations/V004__bakery_onboarding.grants.sql) (thêm `rejected`, lịch sử review, danh mục gói, gói đã chọn, kỳ sử dụng, lời mời). **Đã áp dụng lên Aiven ngày 31/10/2026.** Bốn điều kiện độc lập: user (`status`, `system_role`), membership (`active`/`removed`, `owner`/`staff`), hồ sơ (`profile_status`), quyền gói (kỳ còn hạn). Mọi thao tác theo tiệm kiểm tra membership trong SQL; `bakeryId` trên route chỉ là tham số tra cứu. Tiệm không tồn tại/không phải thành viên → 404 `BAKERY_NOT_FOUND`; staff làm thao tác của owner → 403 `OWNER_REQUIRED`.

| Endpoint | Ai | Ghi chú |
| --- | --- | --- |
| `POST /api/auth/register-bakery` `{ email, password, fullName, phone?, bakeryName }` | ẩn danh | 201 `{ user, bakeryId }`. Một transaction: user `bakery`/`active`/chưa xác minh + tiệm `draft` (slug server sinh, trùng thì thử lại) + owner. Email đã có → 409, không đổi tài khoản đó. Gửi email xác minh |
| `GET /api/subscription-plans` | công khai | Gói `active`; giá null = chưa định giá |
| `GET .../bakeries/{id}/profile` | owner, staff | Hồ sơ, gói đã chọn, quyết định review gần nhất, `missingForSubmit` |
| `PUT .../bakeries/{id}/profile` | owner | Chỉ khi `draft`; lưu được khi chưa đủ thông tin. Khác `draft` → 409 `PROFILE_STATUS_NOT_ALLOWED` |
| `PUT .../bakeries/{id}/subscription/plan` `{ planId }` | owner | Đổi tự do trước khi duyệt; khi còn kỳ sử dụng → 409 `PLAN_CHANGE_NOT_ALLOWED`; gói không mở → 404 `PLAN_NOT_FOUND`; hồ sơ `rejected` → 409 |
| `POST .../bakeries/{id}/profile/submit` | owner, email đã xác minh | Cần `name`, `addressLine`, `city`, `phone` và gói đang mở → `pending_review`. Thiếu → 409 `PROFILE_INCOMPLETE` + `missing`; không phải `draft` → 409 `PROFILE_NOT_DRAFT` |
| `GET .../bakeries/{id}/reviews` | owner, staff | Lịch sử gửi duyệt/quyết định (không có id người xử lý) |
| `GET/DELETE .../bakeries/{id}/members[/{userId}]` | owner | Gỡ staff → `removed` (giữ lịch sử, mất quyền ở request kế tiếp); không gỡ owner (409 `CANNOT_REMOVE_OWNER`) |
| `GET/POST/DELETE .../bakeries/{id}/invitations[/{id}]` | owner (POST cần email đã xác minh) | Lời mời theo email, 7 ngày, dùng một lần, lời mời mới thu hồi lời mời cũ cùng email. 201 kèm `emailDelivered`. Đã là thành viên → 409 `ALREADY_MEMBER` |
| `POST /api/auth/invitations/accept` `{ token }` | đã đăng nhập | Tài khoản `bakery` đúng email → staff (membership removed được kích hoạt lại). Customer/admin → 403 `ROLE_NOT_ALLOWED`, không đổi role. Token sai/hết hạn/đã dùng/khác email → 400 `TOKEN_INVALID` |
| `POST /api/auth/invitations/accept-new` `{ token, password, fullName, phone? }` | ẩn danh | Tạo user `bakery` với email của lời mời (đã xác minh bằng token), staff, nguyên tử. Email đã có tài khoản → 409 `ACCOUNT_EXISTS` |
| `POST /api/admin/bakeries/{id}/review` `{ decision, reason? }` | admin | `approve` → `active` + trial 30 ngày nếu tiệm chưa từng có; `request_changes` → `draft`; `reject` → `rejected` (kết thúc). Lý do bắt buộc khi yêu cầu sửa/từ chối. Không `pending_review` → 409 `BAKERY_NOT_PENDING_REVIEW`; chưa chọn gói → 409 `PLAN_NOT_SELECTED` |
| `GET /api/admin/bakeries/{id}/reviews` | admin | Lịch sử kèm id người xử lý. Danh sách chờ: `GET /api/admin/bakeries?profileStatus=pending_review` |

`.../bakeries/{id}` = `/api/bakery-workspace/bakeries/{bakeryId}`. Review khóa dòng tiệm: xử lý đồng thời chỉ một chuyển trạng thái; trial một lần mỗi tiệm (UNIQUE ở database), duyệt lặp không dời mốc. Từ chối không khóa tài khoản/không gỡ membership. Lời mời và token chỉ lưu SHA-256; link `{App:PublicBaseUrl}/accept-invitation#token=…`.

Chưa có (chưa chốt): sửa hồ sơ sau khi `active`, mở lại hồ sơ `rejected`, chuyển owner, giới hạn số tiệm mỗi owner, chống lặp trial bằng nhiều tiệm.

## Gói, trial và thanh toán phí sử dụng

Đây là phí sử dụng DeCakes của tiệm, **không phải** tiền khách mua bánh. Cần [V005](database/migrations/V005__subscription_payments.sql) + [grants](database/migrations/V005__subscription_payments.grants.sql). **Đã áp dụng lên Aiven ngày 31/10/2026.**

- Quyền tại thời điểm t: tiệm có kỳ (trial/paid) `starts_at <= t < ends_at`, tính mỗi lần kiểm tra (không dựa vào job). Trial = 30 × 24 giờ từ lúc duyệt. Gia hạn: kỳ mới bắt đầu từ max(lúc thanh toán được xác nhận, ngày hết hạn hiện tại), +1 tháng/+1 năm theo lịch UTC (kẹp cuối tháng, 29/02).
- Chặn ở điểm ghi: `sp_send_request_to_bakery`, `sp_create_quote`, `sp_revise_quote`, `sp_accept_quote` gọi `sp_guard_bakery_entitled` **sau khi giữ khóa**, với `v_now` lúc đó. Hết hạn → SIGNAL `BAKERY_SUBSCRIPTION_INACTIVE` (gửi yêu cầu: 409 cùng mã). Báo giá tạo trước khi hết hạn cũng không chọn được. `sp_withdraw_quote`, `sp_bakery_decide_order` không đổi: rút báo giá, xác nhận/từ chối đơn đã có vẫn chạy. Hết hạn không khóa user, không đổi hồ sơ, không hủy đơn. Procedure vẫn tự quản lý transaction.
- Catalog chưa đổi theo quyền gói (hiển thị sau hết hạn chưa chốt).

| Endpoint | Ai | Ghi chú |
| --- | --- | --- |
| `GET .../bakeries/{id}/subscription` | owner, staff | Gói, giá, `accessActive`, `accessEndsAt`, các kỳ |
| `POST .../bakeries/{id}/subscription/checkout` `{ billingCycle: month \| year }` | owner, email đã xác minh, hồ sơ `active` | 201 giao dịch `pending` + `checkoutUrl`. Số tiền lấy từ giá gói ở server và được chốt vào giao dịch. 409 `PLAN_NOT_PRICED` (chưa định giá, không dùng giá 0), `PLAN_NOT_SELECTED`, `PLAN_NOT_AVAILABLE`, `PROFILE_STATUS_NOT_ALLOWED`; 503 `PAYMENT_NOT_CONFIGURED` (không tạo giao dịch, không gọi mạng); 502 `PAYMENT_PROVIDER_ERROR` (giao dịch → `failed`) |
| `GET .../bakeries/{id}/subscription/payments/{paymentId}` | owner | Trạng thái theo dữ liệu đã xác minh ở server. Trang return URL không cấp quyền |
| `POST /api/payments/payos/webhook` | payOS (server-to-server) | Không CSRF/cookie (ngoại lệ chỉ ở action có `[SignedWebhook]`); bắt buộc chữ ký HMAC-SHA256 trên `data`, sai → 401. Đối chiếu mã đơn, số tiền, tiền tệ, mã link dưới khóa; khớp → một kỳ trả phí (UNIQUE `payment_id`), lặp/đến trễ → không đổi; không khớp → `mismatch`, không cấp quyền. Chưa cấu hình → 503 |

Cấu hình payOS (tắt mặc định): `Payments:PayOS:Enabled=true`, `ClientId`, `ApiKey`, `ChecksumKey` (user-secrets/biến môi trường), `ReturnUrl`, `CancelUrl` (HTTPS). Thiếu một giá trị → coi như tắt; ứng dụng và health vẫn chạy. Giá gói nằm ở `subscription_plans` do người vận hành quản lý; migration không tạo gói hay giá nào. **Adapter chưa được kiểm chứng với tài khoản merchant payOS thật; kiểm thử dùng chữ ký tính độc lập và cổng giả chỉ có trong project test.**

**Ảnh hưởng V005 đã có trên Aiven:** hai tiệm `active` cũ chưa có kỳ sử dụng bị chặn nhận yêu cầu/báo giá/đơn mới. Không tự cấp trial, không gắn gói hàng loạt; đơn và dữ liệu cũ giữ nguyên. Cấp quyền sử dụng cho các tiệm này cần phương án riêng được duyệt. Gói Cơ bản đã tạo chưa tự cấp quyền cho tiệm nào.

## Trạng thái xác minh — 16/10/2026

- SDK 10.0.401; restore và build thành công, 0 cảnh báo, 0 lỗi.
- `dotnet test` không có MySQL kiểm thử (sau khi gộp Catalog từ `main`, 17/10/2026): 329 đạt, 0 lỗi, 47 bỏ qua.
  - Fake/in-memory: Application 139 (Accounts, Catalog, DesignRequests, inbox của tiệm), Api 154 (Accounts, Catalog, DesignRequests, inbox của tiệm, TestServer HTTPS, Kestrel HTTPS thật).
  - Infrastructure không cần MySQL: 36 (chốt bảo vệ, bộ tách script DELIMITER, quyền backend, allowlist mã lỗi).
  - MySQL thật: 47 ca bị bỏ qua khi không đặt `DECAKES_TEST_MYSQL_ADMIN`.
- Trên MySQL Community Server 8.4.11 tạm (ZIP chính thức, `127.0.0.1`, cổng ngẫu nhiên, X Plugin tắt, không cài service), trước khi gộp Catalog: **32/32 ca MySQL đạt** (10 Accounts, 14 DesignRequests, 8 inbox của tiệm). 15 ca MySQL của Catalog chưa chạy lại trên fixture đã gộp. Schema và 11 procedure nạp từ SQL nguồn; tài khoản kiểm thử có đúng quyền trong `security/app_user_grants.sql`.
  - DesignRequests: CRUD draft, giá trị biên (DECIMAL, SMALLINT UNSIGNED, TEXT 65 535 byte, mili giây), phạm vi theo chủ sở hữu, danh sách và phân trang, mẫu bánh, gửi tiệm chuyển `open`, gửi trùng, tiệm không active/không tồn tại, pickup đã qua, yêu cầu đã đóng, khách bị disabled, sai chủ sở hữu, SQLSTATE/Number của SIGNAL, backend không INSERT trực tiếp `request_bakeries`, 8 lần gửi đồng thời chỉ tạo một dòng, UPDATE chờ khóa dòng rồi từ chối vì không còn draft, 15 vòng sửa và gửi đồng thời không có lần sửa nào sau khi `open`.
  - Inbox của tiệm: yêu cầu gửi bằng procedure nguồn rồi đọc lại; cách ly giữa các tiệm; owner/staff active đọc được, removed/user disabled/user không phải role `bakery` không đọc được; yêu cầu chưa gửi không thấy; `sent_at` trùng sắp theo id dòng gửi và phân trang ổn định; hai bộ lọc riêng; tiệm không active vẫn xem lịch sử; dữ liệu `request_bakeries`, `design_requests`, `bakery_members` không đổi sau các lần đọc.
- Luồng cookie trên Kestrel HTTPS thật với store trong bộ nhớ: đăng ký → đăng nhập → CSRF mới → `/me` → đăng xuất → `/me` 401.
- Không chạy kiểm thử ghi hay procedure trên Aiven. Trên Aiven chỉ từng chạy truy vấn đọc của Accounts (SELECT theo email và theo id với khóa không tồn tại; kết nối `decakes_app` + `VerifyFull`).

### Kiểm chứng tích hợp main — 21/10/2026

- Sau `git fetch origin`, `HoangDev` và `origin/main` cùng ở `e4de7e2`. Phần DesignRequests/inbox chưa commit được giữ cùng Catalog; sáu trạng thái xung đột khi khôi phục stash đã được xử lý.
- `dotnet restore DeCakes.slnx --disable-parallel -m:1 -nr:false` và `dotnet build DeCakes.slnx --no-restore -m:1 -nr:false`: thành công, 0 cảnh báo, 0 lỗi.
- Kiểm thử không kết nối database, tạm bỏ biến MySQL test khỏi process: `dotnet test DeCakes.slnx --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~MySqlUserAccountStoreTests'`. Application 139 PASS, API 154 PASS, Infrastructure 36 PASS và 37 SKIP; 10 ca Accounts MySQL được loại khỏi lượt này bằng filter.
- MySQL Community Server 8.4.11 chạy tạm trên loopback: `dotnet test tests/DeCakes.Infrastructure.Tests --no-build --no-restore -m:1 -nr:false` đạt 83 PASS, 0 FAIL, 0 SKIP, gồm 36 kiểm tra an toàn và đủ 47 ca MySQL (Accounts, Catalog, DesignRequests, inbox).
- Fixture Catalog dùng `CatalogReadOnly = true` để giữ tài khoản chỉ có SELECT; các fixture nghiệp vụ dùng quyền backend nguồn. Không nới kiểm thử cấm INSERT của Catalog.
- Tổng cộng 376 kiểm thử khác nhau PASS. MySQL tạm đã dừng và xóa: 0 database/user test còn lại, biến môi trường được khôi phục. Không chạy lệnh lên Aiven.

### Kiểm chứng Admin giai đoạn 1 — 22/10/2026

- Restore/build PASS, 0 compile error; warning NU1900 do không tải được metadata vulnerability từ nuget.org (online vulnerability check NOT RUN).
- Regression không database: Application 150 PASS, API 181 PASS, Infrastructure 40 PASS; 0 FAIL. 45 ca persistence SKIP có chủ đích và 10 Accounts MySQL bị loại bằng filter trong lượt này.
- MySQL Community 8.4.13 disposable local: 95 PASS, 0 FAIL, 0 SKIP (40 kiểm tra không database + 55 persistence, gồm 8 ca Admin mới). Tổng cộng 426 kiểm thử khác nhau PASS. Accounts, Catalog, DesignRequests, Inbox, health, OpenAPI và Kestrel HTTPS vẫn qua kiểm thử.
- Đã dọn process/database/user/data/config tạm, khôi phục biến test. Không chạy bootstrap thật hoặc lệnh lên Aiven. Không commit/push/merge/PR; giữ nguyên thay đổi sẵn có trên nhánh `feature/admin-phase1`.
- Hợp đồng API, danh sách file, lệnh kiểm chứng chính xác, hướng dẫn nhập ẩn và giới hạn: [Admin](docs/ADMIN_PHASE1.md), [tests](tests/README.md#admin-giai-đoạn-1).
