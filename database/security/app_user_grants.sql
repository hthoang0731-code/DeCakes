-- =============================================================================
-- DeCakes — security/app_user_grants.sql   (TÙY CHỌN, khuyến nghị)
--
-- Phân quyền cho tài khoản MySQL mà backend ASP.NET Core dùng để kết nối.
--
-- Hai loại "tài khoản" khác nhau:
--   * Tài khoản WEBSITE (bảng users): khách, nhân viên tiệm, admin đăng nhập website.
--     Backend xác thực họ, rồi truyền users.id vào stored procedure.
--   * Tài khoản MYSQL (ở đây: decakes_app): chỉ backend dùng để kết nối database.
--     Người dùng website không bao giờ có tài khoản MySQL.
--
-- Mục đích: backend KHÔNG ghi thẳng được vào quotes, orders (trừ cột trạng thái),
-- request_bakeries; các thao tác đó phải qua stored procedure (SQL SECURITY DEFINER,
-- chạy với quyền của người tạo thủ tục, vd. avnadmin). Nhờ vậy các quy tắc chỉ có
-- trong thủ tục (ảnh thiết kế đúng tiệm, kiểm tra hạn, quyền thành viên, bản chốt
-- đơn) không bị vượt qua bởi một câu INSERT/UPDATE viết nhầm trong backend.
--
-- CÁCH DÙNG
--   1. Tạo user MySQL KHÔNG ghi mật khẩu vào file:
--        Aiven: tạo một user MỚI bằng CREATE USER trong phiên quản trị, không cấp
--        quyền quản trị hay role. Nhập mật khẩu riêng trực tiếp, không lưu vào file.
--        CREATE USER 'decakes_app'@'%' IDENTIFIED BY '<nhập khi chạy>';
--        Hoặc dùng Aiven API tạo service user với mysql_grants = [].
--        Không dùng user Console đã được cấp quyền admin mặc định để chạy script này.
--      MySQL local: CREATE USER 'decakes_app'@'localhost' IDENTIFIED BY '<nhập khi chạy>';
--      (gõ trực tiếp trong DBeaver, không lưu lại vào file).
--   2. Chạy file này bằng tài khoản quản trị (avnadmin / root) SAU install/02.
--      Trên Aiven, user do Console tạo có host '%'. Đổi 'decakes_app'@'%' nếu khác.
--   3. Kiểm tra: SHOW GRANTS FOR 'decakes_app'@'%';
--
-- Ghi chú: DBeaver dùng tài khoản quản trị để cài schema; backend dùng decakes_app.
-- =============================================================================

-- Chỉ dùng cho user mới, chưa có quyền ngoài USAGE. Script không thu hồi quyền
-- quản trị có sẵn. Aiven avnadmin không được thu hồi toàn bộ quyền vì thao tác đó
-- đụng tới các quyền trên database hệ thống mysql.
-- Nếu user đã có quyền rộng, tạo một user mới có tên khác và thay tên dưới đây.

-- Đọc mọi bảng, gọi mọi thủ tục.
GRANT SELECT  ON decakes_db.* TO 'decakes_app'@'%';
GRANT EXECUTE ON decakes_db.* TO 'decakes_app'@'%';

-- Bảng backend được ghi trực tiếp (không có DELETE: prototype đổi trạng thái thay vì xóa).
GRANT INSERT, UPDATE ON decakes_db.users           TO 'decakes_app'@'%';
GRANT INSERT, UPDATE ON decakes_db.bakeries        TO 'decakes_app'@'%';
GRANT INSERT, UPDATE ON decakes_db.bakery_members  TO 'decakes_app'@'%';
GRANT INSERT, UPDATE ON decakes_db.cakes           TO 'decakes_app'@'%';
GRANT INSERT, UPDATE ON decakes_db.design_requests TO 'decakes_app'@'%';

-- Ảnh bất biến: chỉ thêm, không sửa.
GRANT INSERT ON decakes_db.media_assets TO 'decakes_app'@'%';

-- request_bakeries: thêm dòng qua sp_send_request_to_bakery; backend chỉ đổi trạng thái
-- đã xem / từ chối.
GRANT UPDATE (status, decline_reason, responded_at, updated_at)
  ON decakes_db.request_bakeries TO 'decakes_app'@'%';

-- quotes: chỉ qua sp_create_quote / sp_revise_quote / sp_withdraw_quote / sp_accept_quote.
-- (không cấp INSERT/UPDATE)

-- orders: tạo qua sp_accept_quote, xác nhận/từ chối qua sp_bakery_decide_order.
-- Backend chỉ được đổi các cột tiến trình sau khi đã xác nhận (ready, picked_up,
-- cancelled). KHÔNG có quyền với agreed_*, bakery_decided_* => bản chốt và người xác
-- nhận không thể bị sửa từ backend; CHECK chk_orders_decision vẫn chặn việc nhảy từ
-- pending_confirmation sang confirmed khi chưa có người xác nhận.
GRANT UPDATE (status, picked_up_at, cancelled_at, updated_at)
  ON decakes_db.orders TO 'decakes_app'@'%';
