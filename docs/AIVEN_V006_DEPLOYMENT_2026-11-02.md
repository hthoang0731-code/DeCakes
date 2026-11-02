# Triển khai V006 lên Aiven — 02/11/2026

**Cập nhật sau lượt triển khai:** đã nghiệm thu ghi Orders trên Aiven theo phạm vi riêng: 58 request/83 kiểm tra đạt, tạo đúng 18 bản ghi thử và giữ nguyên dữ liệu gốc. Email dùng địa chỉ mô phỏng chính thức của Resend; chưa chứng minh Inbox thật. Xem [báo cáo nghiệm thu](AIVEN_ORDERS_ACCEPTANCE_2026-11-02.md). Các số liệu và giới hạn bên dưới ghi nhận tại thời điểm triển khai migration, trước khi tạo dữ liệu nghiệm thu.

## Kết quả và phạm vi

Theo xác nhận riêng của người dùng, đã chạy đúng `database/migrations/V006__order_decision_time_rules.sql` một lần qua kết nối quản trị DBeaver có sẵn trên `decakes_db`, MySQL 8.4.11. DBeaver ghi nhận 15 câu lệnh, khoảng 1,9 giây, kết thúc không có hộp thoại lỗi. Thông báo `tmp_v006_precheck does not exist` là cảnh báo của `DROP PROCEDURE IF EXISTS` đầu script; precheck sau đó chạy thành công.

| Kiểm tra | Trước | Sau |
| --- | --- | --- |
| schema_migrations.version | 1–5 | 1–6 |
| Bảng | 17 | 17 |
| Procedure | 12 | 12 |
| tmp_v006_precheck | Không có | Không có |
| Hai procedure Orders | Bản V005/install | Khớp nội dung V006 |
| Definer, SQL SECURITY, tham số, charset/collation của hai procedure | Đã lưu | Giữ nguyên; SECURITY DEFINER |
| Dữ liệu 16 bảng nghiệp vụ | Đã lưu dấu đối chiếu | Số dòng, cột và dấu đối chiếu giữ nguyên |
| SHOW GRANTS của decakes_app | Đã lưu | Giữ nguyên |

V006 tạo precheck tạm, thay `sp_accept_quote`/`sp_bakery_decide_order`, xóa precheck tạm và thêm version 6. Không sửa bảng/cột, không chạy grants, không đổi tài khoản/mật khẩu hoặc cấu hình ứng dụng. Không gọi procedure nghiệp vụ, tạo đơn thử, cấp trial, gửi email hay gọi payOS. Không commit/push.

SHA-256 SQL nguồn trước/sau: `F6199D86D7040E7F6FAD080487CA55895890CCC8B6009DF17AF42AF0517BA15E`.

## Sao lưu và đối chiếu

Bản sao trước triển khai chỉ gồm hai procedure bị thay, không phải full backup dữ liệu:

- `TestResults/aiven-v006-20261102/backup/procedures-before.json`: body, tham số, definer, SQL mode và context charset/collation đọc từ server.
- `TestResults/aiven-v006-20261102/backup/restore-procedures.sql`: dựng lại hai CREATE PROCEDURE với context đã lưu; SHA-256 `e18715dc5a3b5d4b26ac34ab7eccc1655d24cdf3e21967181d9adb8f0f1f4b06`.
- Body accept trước triển khai khớp V005; body decide khớp install/02 sau khi bỏ qua một dòng chú thích không còn trên server.
- Chưa phục hồi thử từ bản sao. File restore không chứa DROP và không được tự chạy; phục hồi cần phạm vi riêng. DDL commit ngầm từng câu, migration không atomic và không tự chạy lại an toàn.

Công cụ audit riêng dùng `AddInfrastructure`, factory thực và secret hiện có của tài khoản backend hạn chế. Nó xác minh đúng service/database/account, `VerifyFull` + CA, TLS 1.3 / `TLS_AES_256_GCM_SHA384`. Audit chỉ SELECT/SHOW; không gọi procedure. Hash từng dòng tính tại server trên toàn bộ cột, công cụ chỉ giữ số dòng và dấu tổng, không xuất nội dung nghiệp vụ/token/password hash. `schema_migrations` đối chiếu riêng vì cần thêm version 6.

| Bảng | Số dòng trước/sau không đổi |
| --- | --- |
| users | 5 |
| bakeries | 2 |
| bakery_members | 2 |
| cakes | 2 |
| design_requests | 1 |
| request_bakeries | 2 |
| media_assets | 7 |
| quotes | 3 |
| orders | 1 |
| subscription_plans | 1 |
| bakery_invitations, bakery_review_events, bakery_subscription_periods, bakery_subscriptions, subscription_payments, user_tokens | Mỗi bảng 0 |

## Xác minh mã và vận hành

- Hai body procedure trên server khớp mã V006 sau chuẩn hóa xuống dòng và bỏ chú thích. Tham số, definer và charset/collation khớp bản sao.
- `sp_accept_quote` giữ nguyên creation SQL mode. `sp_bakery_decide_order` được tạo trong context có thêm `STRICT_TRANS_TABLES` so với bản cũ; không loại bỏ mode cũ, gồm `STRICT_ALL_TABLES`. Đã ghi nhận chênh lệch này; không sửa tiếp procedure/context hoặc chạy thử so sánh hành vi các SQL mode trong lượt triển khai.
- `dotnet .../Audit.dll after`: PASS; versions 1–6, 17 bảng, 12 procedure, 16 bảng nghiệp vụ không đổi. `IOrderStore.FindForCustomerAsync(0, long.MaxValue)` trả OrderNotFound bằng SELECT thật, không đọc nội dung đơn hiện có.
- Health HTTPS 200; OpenAPI 200 và có đủ 7 route Orders; GET /api/orders chưa đăng nhập trả 401. Không gửi HTTP request ghi. API đang chạy bản Orders; không cần khởi động lại chỉ để đọc version mới vì store kiểm tra version theo từng thao tác.
- Không chạy lại bộ unit/integration test trong lượt triển khai. Bằng chứng HTTPS + MySQL local trước đó: 98 request/146 kiểm tra đạt, xem ORDERS; không dùng kết quả đó để tuyên bố đã nghiệm thu ghi Orders trên Aiven.

Các file audit/metadata/backup nằm trong `TestResults/aiven-v006-20261102/` bị Git bỏ qua. Không có bí mật trong báo cáo này. Công cụ scratch có một lỗi kiểu dữ liệu lúc build và một lỗi cú pháp PowerShell khi ghi báo cáo HTTP, đã sửa; không ảnh hưởng SQL triển khai. Build công cụ cuối thành công với 4 cảnh báo NU1900 do không truy cập được metadata vulnerability NuGet, không có lỗi.

## Giới hạn sau triển khai

- Điều kiện thiếu version 6 đã được đáp ứng trên Aiven; 503 ORDER_WORKFLOW_NOT_READY do thiếu V006 không còn là chốt của database này. Chưa đăng nhập và thử accept/confirm/reject thật trên Aiven trong lượt này.
- Hai tiệm active cũ vẫn chưa có kỳ sử dụng; V006 không cấp trial/gói cho chúng. Giao dịch mới của các tiệm này vẫn bị guard gói chặn. Giá gói vẫn chưa chốt; không thay cấu hình thanh toán.
- Không thay đổi quy tắc chưa chốt về deadline đơn, hủy đơn, ready/picked_up hoặc ảnh riêng tư.

Xem [Orders](ORDERS.md), [báo cáo V003–V005](AIVEN_DEPLOYMENT_2026-10-31.md) và [database](../database/README.md).