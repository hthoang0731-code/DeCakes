# Kiểm thử

Chạy: `dotnet test` tại thư mục repository.

| Project | Loại | Phạm vi |
| --- | --- | --- |
| `DeCakes.Application.Tests` | Fake | `AccountService` với store và hasher giả (chuẩn hóa email, độ dài, mật khẩu không trim, trùng email, đăng nhập thất bại, rehash). Paging và Catalog services: validation, defaults, filter, lookahead, 64-bit offset, detail outcomes, lỗi và cancellation. `DesignRequestValidator` (giới hạn theo schema, DECIMAL không làm tròn, `pickupAt` có offset/mili giây/tương lai, TEXT theo byte) và `DesignRequestService` với store giả. `BakeryWorkspaceService`: kiểm tra `status`/`requestStatus`/phân trang trước khi truy vấn, mặc định, phân biệt `BAKERY_NOT_FOUND`/`REQUEST_NOT_FOUND`. |
| `DeCakes.Api.Tests` | Fake/in-memory | API qua `WebApplicationFactory` (TestServer scheme HTTPS) và Kestrel HTTPS thật, store user trong bộ nhớ, `PasswordHasher` thật: CSRF, cookie flags và hạn 2 giờ, `/me`, logout, user bị disabled/xóa/đổi role, 401/403, 429, lỗi database được che, health không phụ thuộc database, nhánh hash giả. Catalog (xem mục dưới). DesignRequests với store trong bộ nhớ: 401/403, CSRF, 201 + `Location`, validation, truy cập chéo khách (404), phân trang và lọc, sửa draft/409, gửi tiệm và ánh xạ mọi kết quả procedure sang HTTP, lỗi được che. Inbox của tiệm (`InMemoryBakeryWorkspaceStore` đọc chung dữ liệu với store yêu cầu, nên khách gửi qua API rồi tiệm đọc qua API): 401, customer/admin 403, owner/staff đọc đúng tiệm, removed/tiệm khác/tiệm không tồn tại cùng 404, yêu cầu chưa gửi 404, không lộ thông tin tài khoản khách (đối chiếu đúng tập trường JSON), thứ tự khi `sentAt` trùng, phân trang, hai bộ lọc riêng, tham số sai 400, GET không đổi trạng thái/thời gian, tiệm không active vẫn xem lịch sử, lỗi được che. |
| `DeCakes.Infrastructure.Tests` | Chốt bảo vệ + MySQL thật | Chạy luôn: `SafetyGuardTests`, `ScriptAndErrorMappingTests` (bộ tách script hiểu DELIMITER, quyền backend, allowlist mã SIGNAL đối chiếu với `02_procedures.sql`). MySQL 8.4 thật: `MySqlUserAccountStoreTests`, `MySqlDesignRequestStoreTests` (gồm procedure, đồng thời, khóa dòng), `MySqlBakeryCatalogStoreTests`, `MySqlCakeCatalogStoreTests`, `MySqlBakeryWorkspaceStoreTests` (inbox của tiệm); **bị bỏ qua** nếu chưa đặt `DECAKES_TEST_MYSQL_ADMIN`. |

Kiểm thử API dùng môi trường `Testing` (không nạp user-secrets), Data Protection khóa tạm, `FakeTimeProvider`. Ca Kestrel HTTPS cần chứng chỉ phát triển (`dotnet dev-certs https`); client chỉ tin chứng chỉ đó làm root, vẫn kiểm tra hostname, không tắt xác minh. Thiếu chứng chỉ thì ca này bị bỏ qua.

## Kiểm thử MySQL thật (persistence)

Không dùng Aiven, secret development hay dữ liệu demo. Kiểm thử cần một **MySQL 8.4 chỉ dùng để kiểm thử**, chạy trên loopback.

Chốt bảo vệ (`MySqlTestSettings`):

- Chỉ đọc biến `DECAKES_TEST_MYSQL_ADMIN`; không đọc `ConnectionStrings:DeCakesDb` hay user-secrets.
- Từ chối host không phải `localhost`/`127.0.0.1`/`::1`, chuỗi có `aivencloud`, tài khoản `avnadmin`/`decakes_app`, chuỗi có sẵn `Database`.
- Từ chối server không phải 8.4.x.
- Tự tạo database `decakes_test_<ngẫu nhiên>` và tài khoản `dc_test_<ngẫu nhiên>`, cấp đúng các câu GRANT trong `security/app_user_grants.sql` (chỉ đổi database và tài khoản; câu lệnh khác dạng bị từ chối), rồi xóa cả hai khi xong.
- Nạp `database/install/01_schema.sql` và `02_procedures.sql` gốc sau khi bỏ đúng dòng `USE decakes_db;`, tách câu lệnh bằng `SqlScriptSplitter` (hiểu `DELIMITER`, chuỗi và comment; không split mù theo `;`). Script bị từ chối nếu còn `USE`, `CREATE/DROP/ALTER DATABASE|SCHEMA`, `GRANT`, `REVOKE` hay lệnh tài khoản. Trước khi nạp, kiểm tra `SELECT DATABASE()` là database tạm. Procedure do tài khoản quản trị kiểm thử tạo (DEFINER), như production.

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

Inbox của tiệm (`MySqlBakeryWorkspaceStoreTests`): customer, user tiệm, nhiều tiệm và nhiều membership do tài khoản quản trị tạo; yêu cầu được tạo và gửi bằng `sp_send_request_to_bakery` nguồn qua tài khoản có quyền backend. Kiểm tra: danh sách tiệm chỉ gồm membership active của user active có role `bakery`; owner/staff đọc được, thành viên tiệm khác/removed/user disabled/user không phải role `bakery` không đọc được (kể cả khi bị removed sau lần đọc trước); yêu cầu chưa gửi hoặc gửi tiệm khác không thấy; `sent_at` trùng nhau sắp theo id dòng gửi giảm dần và ghép các trang cỡ 1–3 ra đúng thứ tự đầy đủ; lọc `status` và `requestStatus` riêng; ánh xạ đủ cột chi tiết; tiệm `suspended`/`pending_review`/`draft` vẫn xem lịch sử; ảnh chụp toàn bộ cột `request_bakeries`, `design_requests` và `bakery_members` trước/sau các lần đọc không đổi.

## Kiểm thử Catalog

- API tests dùng store trong bộ nhớ: bốn GET, public DTO fields, JSON null/decimal, paging/filter, 200/400/404/500, cookie/session regression. Không dùng kết quả fake để khẳng định SQL đúng.
- Persistence tests dùng database tạm riêng cho từng test với `CatalogReadOnly = true`: tài khoản store chỉ có SELECT. Helper setup dùng kết nối quản trị kiểm thử để INSERT bakery/cake sau khi kiểm tra tên database và `SELECT DATABASE()`; không tăng quyền ghi cho tài khoản đọc Catalog. Fixture Accounts/DesignRequests/inbox vẫn dùng đúng quyền backend nguồn.
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

- Báo giá đã có kiểm thử phần đọc: `QuoteReadServiceTests` (14), `QuoteReadApiTests` (23) và `MySqlQuoteReadStoreTests` (7). Các ca MySQL dùng fixture loopback hiện có và procedure nguồn để chuẩn bị phiên bản, gồm kiểm chứng OUT create/revise; không ghi Aiven. Chi tiết phạm vi phần đọc và quy tắc đã chốt cho phần ghi tiếp theo trong [QUOTES](../docs/QUOTES.md).

- `DeCakes.Domain.Tests`: quy tắc nghiệp vụ khi có.
- Kiểm thử OUT parameter khi triển khai Quotes (`sp_create_quote`, `sp_revise_quote`).

## Admin giai đoạn 1

- Application: validation trước store, defaults/trim, enum, search dài/ký tự điều khiển, id, cancellation/error và forwarding bộ lọc demo.
- API/in-memory: đủ năm endpoint 401/403/200, admin disabled/đổi role mất phiên, query/id sai 400, detail 404, tổng trước phân trang, tìm literal, demo true/false/tất cả, JSON đúng tập trường không có hash, che lỗi, GET không ghi; health/OpenAPI và chỉ có GET Admin.
- Bootstrap không database: hasher Microsoft thật, chuẩn hóa, mật khẩu không trim, dữ liệu sai không ghi, trùng email dừng, hai lần nhập ẩn, từ chối CLI args/redirect, lỗi không lộ bí mật.
- `MySqlAdminStoreTests`: database riêng với `CatalogReadOnly=true` (SELECT-only), đủ trạng thái user/tiệm, nullable/UTC, literal `%/_/!/backslash/quote`, Unicode/collation, input SQL injection, totalCount, thứ tự ổn định, offset 64-bit, dashboard đủ bucket và demo đơn lấy từ request; snapshot cả 9 bảng không đổi sau GET. Không thay fixture Catalog hoặc quyền backend write-tests.
- `MySqlAdminBootstrapTests`: database riêng dùng quyền backend nguồn; console chạy qua prompt giả để ghi thật local, hash xác minh được, fixed role/status/demo, email trùng customer/bakery/admin không đổi và cạnh tranh chỉ tạo một user. Không gọi công cụ production hoặc Aiven.

Lệnh restore/build tại repository:

```powershell
dotnet restore DeCakes.slnx --disable-parallel -m:1 -nr:false
dotnet build DeCakes.slnx --no-restore -m:1 -nr:false
```

Lệnh regression không database (phải bỏ biến test khỏi process và khôi phục, không in giá trị):

```powershell
$savedTestAdmin = [Environment]::GetEnvironmentVariable('DECAKES_TEST_MYSQL_ADMIN', 'Process')
try {
    [Environment]::SetEnvironmentVariable('DECAKES_TEST_MYSQL_ADMIN', $null, 'Process')
    dotnet test DeCakes.slnx --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~MySqlUserAccountStoreTests' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Database-free tests failed' }
}
finally {
    [Environment]::SetEnvironmentVariable('DECAKES_TEST_MYSQL_ADMIN', $savedTestAdmin, 'Process')
}
```

Trên MySQL 8.4 disposable được chỉ định, fixture loopback ở trên vẫn là điều kiện bắt buộc; biến test chỉ có trong process của runner, không lấy development secret. Lệnh persistence chính xác:

```powershell
dotnet test tests/DeCakes.Infrastructure.Tests --no-build --no-restore -m:1 -nr:false --verbosity minimal
```

Kiểm chứng mới ngày 22/10/2026:

- Restore/build: PASS, 0 compile error; warning NU1900 vì không tải được dữ liệu vulnerability từ nuget.org. Kiểm tra vulnerability online: NOT RUN. Không thêm dependency vào bốn source project.
- Database-free: PASS Application 150, API 181, Infrastructure 40 (tổng 371), FAIL 0. 10 MySQL Accounts bị loại bởi filter; 45 persistence tests khác SKIP có chủ đích khi không có biến test. Ca Kestrel HTTPS thật và OpenAPI đều PASS.
- MySQL 8.4.13 local: PASS 95, FAIL 0, SKIP 0, gồm 40 kiểm thử không database và 55 persistence (47 regression + 8 Admin). Lần đầu có 1 phép thử thêm đổi SQL mode nhưng không đồng bộ escaping connector bị FAIL; bỏ phép thử cấu hình không hợp lệ rồi chạy lại toàn bộ đạt 95/95.
- Dọn fixture: xác nhận 0 database `decakes_test_*`/user `dc_test_*` còn lại, dừng đúng process MySQL task; runtime/data/config/password files đã xóa, biến test khôi phục ở finally. Lần đầu Windows để lại process con; đã nhận diện bằng đúng executable/runtime và dọn, lượt cuối dùng shutdown graceful và `--no-monitor`.
- Lượt test trong sandbox bị lỗi kết nối testhost và được dừng; lượt hợp lệ chạy lại ngoài giới hạn tiến trình, không thay target dữ liệu. Không chạy lệnh nào lên Aiven.
- Tổng 426 kiểm thử khác nhau PASS (371 không database + 55 persistence); `git diff --check`/`git diff --cached --check` và liên kết local trong README/tests/ADMIN_PHASE1: PASS. Nhánh `feature/admin-phase1` từ `e4de7e2`; không thay index/stash, không commit/push/merge/PR.

## Accounts: xác minh email, khôi phục/đổi mật khẩu, thu hồi phiên

- Fixture MySQL nạp V003–V005 theo `SchemaScript.MigrationFiles` sau `install/01`, `02`, và cấp thêm grants `*.grants.sql` tương ứng cho tài khoản kiểm thử. Fixture chỉ chạy trên database tạm. Aiven đã được triển khai riêng ngày 31/10/2026; không dùng fixture để kiểm thử ghi trên Aiven. Xem [báo cáo triển khai](../docs/AIVEN_DEPLOYMENT_2026-10-31.md).
- Application (`AccountSecurityServiceTests`, `AccountTokensTests`): token 43 ký tự base64url ↔ SHA-256, token sai định dạng không ném lỗi; quên mật khẩu chỉ cấp cho user active không demo; throttle 60 giây và 5 lần/giờ; kiểm tra mật khẩu mới trước khi chạm store; đổi mật khẩu xác minh mật khẩu hiện tại và truyền hash mong đợi; xung đột hash.
- API (`AccountSecurityApiTests`, store/email trong bộ nhớ): đăng ký gửi link, user chưa xác minh đăng nhập được nhưng nhận 403 `EMAIL_NOT_VERIFIED` khi tạo/gửi yêu cầu (trước validation body); token dùng lại/sai/hết hạn/sai mục đích/bị thu hồi/email đổi/user disabled cùng 400 `TOKEN_INVALID`; 8 lần confirm/reset đồng thời chỉ 1 thành công; quên mật khẩu trả cùng 202 rỗng cho email có/không/disabled/demo; rate limit nhóm khôi phục; log không chứa email hay token; reset làm mất hiệu lực cookie ở hai trình duyệt, không mở lại user disabled, mật khẩu sai quy tắc không tiêu thụ token; đổi mật khẩu giữ phiên hiện tại, thu hồi phiên khác, cần CSRF/mật khẩu hiện tại; cookie không có claim `decakes:sv` bị từ chối; admin chưa xác minh vẫn dùng API Admin.
- Infrastructure không database (`ResendAccountEmailSenderTests`, handler HTTP giả, logger ghi lại): Bearer key, header `User-Agent` (Resend trả 403 nếu thiếu), JSON `from/to/subject/text/html`, link fragment theo `App:PublicBaseUrl`, HTML encode họ tên, cấu hình thiếu/HTTP/placeholder không gọi mạng và log nêu đúng tên khóa thiếu (không nêu giá trị), lỗi nhà cung cấp không lộ token/khóa/nội dung phản hồi. Log khi Resend nhận có id email; log khi bị từ chối có mã HTTP và tên lỗi (`validation_error`…), bỏ `message` vì có thể chứa địa chỉ email; body lạ hoặc có xuống dòng ghi `unknown`. Không log nào chứa địa chỉ người nhận, token, `#token=` hay khóa. **Không chứng minh Resend thật.**
- MySQL (`MySqlAccountSecurityStoreTests`): V003 thêm cột với user cũ chưa xác minh, version 3; chỉ lưu digest; cấp token thu hồi token cũ cùng mục đích; confirm/reset một lần, từ chối sai mục đích/hết hạn/email đổi/user disabled/demo mà không đổi dòng; reset thu hồi token đặt lại khác; 10 lần dùng đồng thời một token chỉ một lần thành công; đổi mật khẩu so hash nhị phân, chặn user disabled, thu hồi token đặt lại; tài khoản backend không có DELETE trên `user_tokens`.

## Onboarding tiệm, staff, review và gói

- Fixture MySQL nạp thêm V004 và V005 (đề xuất). Từ V005, tiệm cần kỳ sử dụng còn hạn để nhận yêu cầu: `MySqlTestDatabase.GrantAccessAsync` cấp kỳ trial cho tiệm seed trong các test DesignRequests/Inbox có gửi yêu cầu.
- MySQL (`MySqlBakeryOnboardingStoreTests`): đăng ký chủ tiệm nguyên tử (user/tiệm/owner), email trùng không đổi tài khoản cũ và không để tiệm mồ côi, 8 lần đăng ký đồng thời một email chỉ tạo một bộ; slug khác nhau khi trùng tên; hồ sơ chỉ thành viên active xem, chỉ owner sửa khi `draft`; gửi duyệt cần đủ trường và gói đang mở, ghi sự kiện; approve tạo một trial 30 ngày với gói đã chọn, duyệt lặp không thêm trial; 12 quyết định đồng thời chỉ một chuyển trạng thái; yêu cầu sửa/từ chối có lý do, từ chối là kết thúc, không khóa tài khoản/không gỡ membership; CHECK/UNIQUE của database chặn trial thứ hai, lý do rỗng, trạng thái lạ; không đổi gói khi còn kỳ, đổi được đúng mốc hết hạn; gỡ staff mất quyền Inbox ngay, không gỡ owner; lời mời owner-only, thay lời mời cũ, chặn thành viên hiện có; nhận lời mời một lần, đúng email, customer/admin không bị nâng role; kích hoạt lại staff đã gỡ; tạo tài khoản qua lời mời nguyên tử và đồng thời chỉ một lần; tài khoản backend không sửa lịch sử/danh mục gói.
- MySQL (`MySqlSubscriptionStoreTests`): tiệm active không có kỳ bị chặn nhận yêu cầu; hết hạn chặn gửi yêu cầu, tạo/sửa báo giá, chọn báo giá đã tạo trước khi hết hạn, nhưng xác nhận đơn cũ và rút báo giá vẫn chạy, không đổi user/hồ sơ; kỳ hết hạn trong lúc procedure chờ khóa vẫn bị chặn (kiểm tra sau khóa); checkout chốt giá server cho owner tiệm active, từ chối gói chưa định giá/không mở/chưa chọn và hồ sơ chưa duyệt; đổi giá danh mục không đổi giao dịch đã tạo; webhook đã xác minh tạo một kỳ nối tiếp phần trial còn lại theo tháng lịch, thanh toán trễ bắt đầu lúc trả và không mở hồ sơ suspended; 10 webhook đồng thời chỉ gia hạn một lần; hai thanh toán đồng thời của một tiệm không chồng kỳ; sai số tiền/tiền tệ/mã link → `mismatch`, không cấp quyền; mã đơn lạ không làm gì.
- Không database: `PayOsPaymentGatewayTests` (chữ ký request/webhook so với giá trị tính độc lập bằng openssl, sửa dữ liệu làm chữ ký sai, `success`/`code` khác thành công không tính là đã trả, cấu hình thiếu/HTTP/placeholder coi như tắt và không gọi mạng, lỗi nhà cung cấp không lộ khóa); `BakeryOnboardingRulesTests` (slug, giới hạn trường, điều kiện gửi duyệt, chu kỳ tháng/năm theo lịch, giữ ngày còn lại).
- API (store/cổng giả, service thật): đăng ký chủ tiệm cố định role, policy Bakery/Admin, user id từ phiên, CSRF, xác minh email trước gửi duyệt/mời staff/checkout, ánh xạ mã lỗi; tình trạng gói tính lúc đọc và hết đúng mốc; checkout không tạo giao dịch khi cổng tắt, lỗi nhà cung cấp → 502 và `failed`; không có đường kích hoạt ngoài webhook; webhook không cần CSRF nhưng cần chữ ký, các POST khác vẫn cần CSRF.
- **Không chứng minh** Resend hay payOS thật: không có tài khoản, domain, merchant, giá; không gửi email hay thu tiền thật.
