# Database DeCakes

Các script SQL của `decakes_db` (MySQL 8.4, Aiven), schema phiên bản 2. Đây là bản sao nguyên vẹn từ gói đã sửa `DeCakes_DB_v2_da_test_Aiven.zip` của nhóm. Nội dung và byte được giữ nguyên (đối chiếu SHA-256 khi sao chép).

> **Cảnh báo.** Database `decakes_db` trên Aiven **đã được cài và kiểm thử**. Không chạy lại script cài mới (`install/*`) hay migration trên database đó. Các script ở đây để tham chiếu, đọc hiểu và dựng database thử riêng, không phải để chạy lại trên database hiện có. Tài liệu này không đưa lệnh nào có thể thay đổi database.

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
- Trạng thái database thật (đã cài đúng v2, đã áp dụng script quyền hay chưa) chưa được xác minh trong repository này.
