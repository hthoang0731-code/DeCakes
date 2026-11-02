# Database DeCakes

Các script SQL gốc của `decakes_db` (MySQL 8.4, Aiven) bắt đầu từ schema phiên bản 2. Bản gốc từ `DeCakes_DB_v2_da_test_Aiven.zip` giữ nguyên byte; các migration V003–V006 là phần bổ sung sau đó. **Aiven đã lên version 6 ngày 02/11/2026**, vẫn có 17 bảng và 12 procedure. Xem [báo cáo V006](../docs/AIVEN_V006_DEPLOYMENT_2026-11-02.md) và [triển khai V003–V005](../docs/AIVEN_DEPLOYMENT_2026-10-31.md).

> **Cảnh báo.** Database `decakes_db` trên Aiven đã cài và áp dụng V003–V006 theo yêu cầu riêng của người dùng. Không chạy lại `install/*`, V002 hay V003–V006 trên database đó. Mọi thay đổi tiếp theo cần migration và phạm vi triển khai riêng; API không tự áp dụng migration khi khởi động.

## Tài liệu nên đọc

| Tài liệu | Nội dung |
| --- | --- |
| [SCHEMA.md](SCHEMA.md) | Bảng, cột, ràng buộc, quan hệ, trạng thái; cách database bảo đảm một yêu cầu tạo tối đa một đơn |
| [STORED_PROCEDURES.md](STORED_PROCEDURES.md) | 11 stored procedure: tham số, lỗi, transaction, khóa, lưu ý khi gọi từ backend |
| [../docs/BACKEND_DATABASE_PLAN.md](../docs/BACKEND_DATABASE_PLAN.md) | Kế hoạch ADO.NET/MySqlConnector, cách gọi procedure, thứ tự triển khai, các điểm cần chốt; không scaffold DbContext |

Hai tài liệu SCHEMA và STORED_PROCEDURES lập từ các file SQL bên dưới, không từ tài liệu tóm tắt. Chưa kết nối hay chạy trên database nào.

## Phân loại script

| Loại | File | Ghi chú |
| --- | --- | --- |
| Cài mới | `install/00_create_database_local.sql` | Chỉ cho MySQL máy cá nhân; Aiven đã có `decakes_db` |
| Cài mới | `install/01_schema.sql` | Tạo 9 bảng nghiệp vụ + `schema_migrations` vào database **trống** |
| Cài mới | `install/02_procedures.sql` | Xóa rồi tạo lại 11 procedure |
| Dữ liệu demo | `install/03_seed_demo.sql` | Dữ liệu hư cấu; chỉ chạy một lần trên database mới |
| Migration | `migrations/V002__order_snapshot_quote_revisions.sql` | Nâng schema v1 lên v2 và giữ dữ liệu. Không dùng cho database đã ở v2 |
| Migration **đã áp dụng 31/10/2026** | `migrations/V003__account_security.sql` (+ `.grants.sql`) | Xác minh email, token khôi phục mật khẩu, session version |
| Migration **đã áp dụng 31/10/2026** | `migrations/V004__bakery_onboarding.sql` (+ `.grants.sql`) | `rejected`, lịch sử review, gói, kỳ sử dụng/trial, lời mời staff |
| Migration **đã áp dụng 31/10/2026** | `migrations/V005__subscription_payments.sql` (+ `.grants.sql`) | Giao dịch phí sử dụng, kỳ trả phí, guard quyền gói trong 4 procedure. Hai tiệm active cũ chưa có kỳ đang bị chặn giao dịch mới |
| Migration **đã áp dụng 02/11/2026** | `migrations/V006__order_decision_time_rules.sql` (không có `.grants.sql`) | Tạo lại `sp_accept_quote`, `sp_bakery_decide_order`: giờ nhận phải còn ở tương lai khi chọn/xác nhận (kiểm tra sau mọi lần đọc có khóa), từ chối bắt buộc lý do. Không đổi bảng/quyền backend. Đã kiểm thử local, triển khai và nghiệm thu ghi Orders trên Aiven bằng email mô phỏng Resend (xem báo cáo nghiệm thu trong ORDERS). DDL commit ngầm từng câu, không atomic. Xem [ORDERS](../docs/ORDERS.md) |
| Quyền backend | `security/app_user_grants.sql` | Tùy chọn. Cấp quyền cho tài khoản MySQL mới của backend, không dùng cho tài khoản quản trị |
| Kiểm thử | `tests/10_single_connection_tests.sql` | Bộ kiểm thử một connection, chỉ chạy trên database thử |
| Kiểm thử | `tests/20_concurrency_setup.sql` | Chuẩn bị dữ liệu cho kiểm thử đồng thời làm tay |
| Kiểm thử | `tests/fixtures/v1/*.sql` | Schema v1 cũ, chỉ dùng để kiểm thử migration |

Chỉ có file `.sql` được đưa vào repository. Hướng dẫn cài đặt và kiểm thử đầy đủ, bộ chạy Python và báo cáo kiểm thử nằm trong gói ZIP nguồn:
`C:\Users\ASUS\Documents\Codex\2026-09-19\files-mentioned-by-the-user-notice\outputs\decakes-database\DeCakes_DB_v2_da_test_Aiven.zip`.

## Quy tắc khi làm việc với các script này

- Không dán mật khẩu, chuỗi kết nối hay CA certificate vào file nào trong repository. Chuỗi kết nối của backend đặt bằng `dotnet user-secrets` hoặc biến môi trường (xem README gốc).
- `security/app_user_grants.sql` chứa chỗ giữ mật khẩu dạng chú thích (`<nhập khi chạy>`); không thay bằng mật khẩu thật.
- Không dùng `avnadmin` hay tài khoản quản trị làm tài khoản kết nối lâu dài của backend.
- Không sửa nội dung các script đã sao chép. Nếu schema hoặc procedure cần đổi, tạo migration mới và thống nhất trong nhóm trước.
- Ngày 31/10/2026 đã xác minh Aiven có versions 1–5 và grants bổ sung cho `decakes_app`/`decakes_app_member2`. Chú thích “đề xuất/chưa áp dụng” trong SQL là trạng thái lúc soạn; không sửa SQL nguồn sau triển khai. SCHEMA/STORED_PROCEDURES ban đầu mô tả v2, cần đọc cùng các migration và báo cáo triển khai.
