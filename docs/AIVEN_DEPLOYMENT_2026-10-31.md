# Triển khai Aiven — 31/10/2026

## Phạm vi và kết quả

Người dùng yêu cầu áp dụng migration lên Aiven và đồng ý tạo một gói **Cơ bản** chưa định giá. Đã áp dụng lần lượt V003, V004, V005 trên `decakes_db`, MySQL 8.4.11, qua kết nối quản trị DBeaver có sẵn. Không chạy lại `install/*` hoặc V002. Không sửa nội dung SQL nguồn, mã ứng dụng, secret, tài khoản MySQL hoặc mật khẩu. Không commit/push.

| Kiểm tra | Trước | Sau |
| --- | --- | --- |
| `schema_migrations.version` | 1, 2 | 1, 2, 3, 4, 5 |
| Bảng | 10 | 17 |
| Procedure | 11 | 12 |
| Procedure mới | — | `sp_guard_bakery_entitled` |
| Procedure gọi guard quyền gói | 0 | Đủ 4: gửi yêu cầu, tạo/sửa báo giá, chọn báo giá |
| Procedure `SQL SECURITY DEFINER` | 11 | 12 |
| Procedure precheck tạm còn lại | 0 | 0 |

V003 thêm `users.email_verified_at`, `users.session_version`, `user_tokens`. V004 thêm trạng thái hồ sơ `rejected` và 5 bảng onboarding. V005 thêm `subscription_payments`, liên kết kỳ trả phí và guard quyền gói. Bốn user cũ giữ `email_verified_at=NULL`, `session_version=0`; không tự xác minh hoặc đặt mật khẩu cho tài khoản demo.

## Sao lưu và giữ dữ liệu

Backup trước triển khai: `TestResults/aiven-deployment-20261031/backup/dump-decakes_db-202610311634.sql`, 61.678 byte. Thư mục `TestResults/` bị Git bỏ qua. Backup chứa dữ liệu tài khoản/nghiệp vụ, chỉ giữ cục bộ, không đưa lên Git hoặc chat.

- SHA-256: `8427603E5AE5ECA36121DD8EC6F47BC2F2F54BE3EEEAD99605F4EFF7148E20FF`.
- DBeaver dùng mysqldump với `--single-transaction`, `--routines`, `--no-tablespaces`, `--set-gtid-purged=OFF`, `--column-statistics=0`, `--ssl-mode=VERIFY_IDENTITY` và CA hiện có. DBeaver tự tải client MySQL 8 trong bước này.
- File có đủ 10 CREATE TABLE, 11 CREATE PROCEDURE, 10 INSERT và dấu hoàn thành; task kết thúc không báo lỗi.
- **Chưa thực hiện phục hồi thử từ backup.** Không coi kiểm tra cấu trúc file là bằng chứng restore thành công. MySQL DDL không rollback bằng transaction ứng dụng; nếu cần phục hồi phải lập kế hoạch riêng trước khi thực hiện, không tự chạy dump lên Aiven.
- Trước và sau migration, công cụ tạm qua DI/factory thật tính SHA-256 từng dòng tại server trên các cột gốc, rồi so số dòng và dấu tổng. Không xuất nội dung dòng hoặc password hash vào log. Cả 9 bảng nghiệp vụ giữ nguyên dữ liệu:

| Bảng | Số dòng không đổi |
| --- | --- |
| users | 4 |
| bakeries | 2 |
| bakery_members | 2 |
| cakes | 2 |
| design_requests | 1 |
| request_bakeries | 2 |
| media_assets | 7 |
| quotes | 3 |
| orders | 1 |

## Quyền và gói

Áp dụng đủ grants đi kèm V003–V005 cho `decakes_app@%` và `decakes_app_member2@%`; user thứ hai dùng cùng nội dung grants, chỉ thay tên account. Không tạo user mới và không tăng quyền quản trị. Kiểm tra SHOW GRANTS/metadata xác nhận:

- `SELECT, EXECUTE` trên `decakes_db.*`.
- `INSERT, UPDATE` trên `user_tokens`, `bakery_subscriptions`, `bakery_invitations`, `subscription_payments`.
- Chỉ `INSERT` trên `bakery_review_events`, `bakery_subscription_periods`.
- Giữ quyền nghiệp vụ cũ; không cấp quyền ghi `subscription_plans` cho backend, không ghi trực tiếp `quotes`, không DELETE/DDL/GRANT OPTION/quyền quản trị.

Đã tạo duy nhất một gói `code=basic`, `name=Cơ bản`, `status=active`, `currency=VND`, `monthly_price=NULL`, `yearly_price=NULL`. Gói này chọn được nhưng chưa mua được; không đặt giá 0, không tạo giao dịch hoặc kỳ sử dụng. Trial 30 ngày vẫn chỉ phát sinh khi admin duyệt hồ sơ mới theo luồng hiện có.

**Hai tiệm active cũ không có kỳ sử dụng:** sau V005 bị chặn nhận yêu cầu, tạo/sửa báo giá và tạo đơn mới. Không tự gắn gói/cấp trial, không đổi hồ sơ/membership/user, không hủy đơn. Bảng `bakery_subscriptions`, `bakery_subscription_periods` còn trống. Xử lý các tiệm này là phạm vi riêng cần quyết định của người dùng.

## Xác minh thực tế

Build: `dotnet build DeCakes.slnx --no-restore -m:1 -nr:false` PASS, 0 lỗi, 1 cảnh báo NU1900 do không truy cập được nguồn metadata vulnerability NuGet của AdminBootstrap.

Kiểm thử không dùng database: tạm xóa biến `DECAKES_TEST_MYSQL_ADMIN` ở Process, khôi phục trong finally; chạy `dotnet test DeCakes.slnx --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~MySqlUserAccountStoreTests' --verbosity minimal --logger 'trx;LogFilePrefix=predeployment' --results-directory TestResults/aiven-deployment-20261031/tests`.

| Nhóm | PASS | FAIL | SKIP |
| --- | --- | --- | --- |
| Application | 191 | 0 | 0 |
| API, fake/in-memory và Kestrel HTTPS | 262 | 0 | 0 |
| Infrastructure không database | 64 | 0 | 85 |

10 ca MySQL Accounts được loại bằng filter; 85 ca MySQL khác bỏ qua vì không có môi trường test. Đây không phải bằng chứng persistence ghi trên Aiven. Lượt đầu trong môi trường hạn chế gặp lỗi Windows Event Log/chứng chỉ; lượt chạy lại với quyền phù hợp đạt như bảng trên, không sửa mã/assertion để đạt PASS.

Công cụ kiểm tra tạm trong `TestResults/aiven-deployment-20261031/audit` dùng `AddApplication`, `AddInfrastructure`, user-secrets và `IMySqlConnectionFactory` thật. Chỉ đọc; không gọi procedure nghiệp vụ. Xác minh `decakes_app@%`, `VerifyFull` + CA, TLS 1.3 / `TLS_AES_256_GCM_SHA384`. Đọc thật qua store Accounts, AccountSecurity/token stats, danh mục gói và Admin dashboard đạt; dashboard đếm 4 user, 2 tiệm, 1 đơn. Không xuất dữ liệu cá nhân.

API đang chạy tại `https://localhost:7215`, giữ xác minh chứng chỉ HTTPS mặc định:

| Request | HTTP thực tế |
| --- | --- |
| GET /api/health | 200 |
| GET /openapi/v1.json | 200 |
| GET /api/auth/csrf | 200, đúng header X-CSRF-TOKEN |
| GET /api/bakeries | 200 |
| GET /api/cakes | 200 |
| GET /api/subscription-plans | 200, có gói Cơ bản và hai giá NULL |
| GET /api/auth/me | 401 khi chưa đăng nhập |
| GET /api/admin/dashboard | 401 khi chưa đăng nhập |
| GET /api/bakery-workspace/bakeries | 401 khi chưa đăng nhập |
| GET /api/design-requests | 401 khi chưa đăng nhập |
| POST /api/auth/login, CSRF hợp lệ, email ngẫu nhiên không tồn tại | 401, không cấp cookie đăng nhập |

## Chưa hoàn tất để dùng toàn bộ chức năng

- **Cập nhật 17:05 ngày 31/10/2026 (giờ Việt Nam):** đã tạo một admin thật (`active`, `is_demo=0`) bằng công cụ vận hành và mật khẩu do người dùng nhập ẩn. Kết quả công cụ: `Created=true`, `ApiVerified=true`, `Stage=complete`. Đăng nhập HTTPS, `/api/auth/me` đúng danh tính/role admin, `/api/admin/dashboard` trả 200; đăng xuất sau xác minh trả 204. Không lưu mật khẩu, token hoặc cookie vào báo cáo. Số dòng users=4 và dấu đối chiếu phía trên là thời điểm ngay sau migration, trước khi chủ động thêm admin này; không dùng lại baseline đó để kết luận user mới là thay đổi ngoài ý muốn. Tài khoản demo chưa được sửa.
- Đã kiểm tra cấu hình hiệu lực qua DI: email chưa đủ các khóa cần thiết; payOS `IsEnabled=false`. Cần tài khoản/domain gửi email, cấu hình Resend và frontend nhận link để xác minh email/quên mật khẩu/mời staff dùng thật. Không tự bỏ chốt email để nghiệm thu.
- Giá gói chưa chốt; payOS chưa có merchant/config đủ, webhook công khai và chưa có thanh toán thật. Không thể coi checkout/gia hạn đã nghiệm thu.
- Chưa nghiệm thu đăng ký → xác minh → đăng nhập → gửi hồ sơ → duyệt → trial hoặc các thao tác ghi nghiệp vụ trên Aiven. Không chạy fixture cài/seed/xóa database kiểm thử lên Aiven.
- Postman vẫn giữ chốt tắt request ghi; không đổi `databaseTarget` thành `isolated-local` khi API thực tế dùng Aiven.
- Báo giá/đơn hàng chưa có API; migration không bổ sung các API này hoặc giao diện.

SQL nguồn giữ nguyên SHA-256 khi triển khai:

| File | SHA-256 |
| --- | --- |
| V003__account_security.sql | `8B5DBA1087AAE4D5EF35C8FC59B9F720F2A206A73DCE18DEC4B7541ADBE526AA` |
| V004__bakery_onboarding.sql | `8D1115CE09729A7C010B404B88619B94143AE37A723E145B513C03C52E12199A` |
| V005__subscription_payments.sql | `DA8EEB379A26B988C619ADD65A1E58C473B953CD90201D2E68B97ABF4B0E23EF` |
