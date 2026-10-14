-- FIXTURE KIỂM THỬ v1 (commit a9a238e) — chỉ dùng để kiểm thử migration V002.
-- =============================================================================
-- DeCakes — 03_seed_demo.sql
-- DỮ LIỆU DEMO. Toàn bộ tiệm, người dùng, yêu cầu, báo giá và đơn hàng dưới đây
-- là hư cấu, chỉ để trình diễn luồng nghiệp vụ. Chúng KHÔNG phải đối tác hay giao
-- dịch có thật.
--
-- Cách đánh dấu:
--   * is_demo = 1 trên users, bakeries, cakes, design_requests, media_assets.
--   * Tên/tiêu đề có tiền tố "[DEMO]".
--   * Email dùng đuôi .invalid (RFC 2606, không thể tồn tại thật).
--   * URL ảnh trỏ tới example.com (tên miền dành riêng cho ví dụ).
--   * password_hash = NULL: tài khoản demo không đăng nhập được, DB không chứa mật khẩu.
--   * Không có số điện thoại hay địa chỉ thật.
--
-- Kịch bản: khách demo tạo 1 yêu cầu -> gửi cho 2 tiệm demo -> 2 báo giá ->
--           khách chọn báo giá của Tiệm B -> Tiệm B xác nhận đơn.
--
-- Chạy sau 01_schema.sql và 02_procedures.sql, trên database vừa tạo mới.
-- =============================================================================

SET NAMES utf8mb4;
SET time_zone = '+00:00';
SET SQL_SAFE_UPDATES = 0;   -- MySQL Workbench bật mặc định; script chỉ cập nhật theo khóa
USE decakes_db;

SET @now = UTC_TIMESTAMP(3);

-- -----------------------------------------------------------------------------
-- Người dùng
-- -----------------------------------------------------------------------------
INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('khach.demo@demo.decakes.invalid',  NULL, '[DEMO] Khách hàng mẫu',   'customer', 1);
SET @customer_id = LAST_INSERT_ID();

INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('chu.tiem.a@demo.decakes.invalid',  NULL, '[DEMO] Chủ Tiệm A',        'bakery',   1);
SET @owner_a_id = LAST_INSERT_ID();

INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('chu.tiem.b@demo.decakes.invalid',  NULL, '[DEMO] Chủ Tiệm B',        'bakery',   1);
SET @owner_b_id = LAST_INSERT_ID();

INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('admin.demo@demo.decakes.invalid',  NULL, '[DEMO] Quản trị viên',     'admin',    1);

-- -----------------------------------------------------------------------------
-- Tiệm bánh (hư cấu) và thành viên
-- -----------------------------------------------------------------------------
INSERT INTO bakeries (name, slug, description, address_line, city, profile_status, is_demo) VALUES
  ('[DEMO] Tiệm Bánh A', 'demo-tiem-banh-a',
   'Tiệm bánh hư cấu dùng để trình diễn DeCakes. Không phải đối tác thật.',
   'Địa chỉ demo – không có thật', 'Thành phố demo', 'active', 1);
SET @bakery_a_id = LAST_INSERT_ID();

INSERT INTO bakeries (name, slug, description, address_line, city, profile_status, is_demo) VALUES
  ('[DEMO] Tiệm Bánh B', 'demo-tiem-banh-b',
   'Tiệm bánh hư cấu dùng để trình diễn DeCakes. Không phải đối tác thật.',
   'Địa chỉ demo – không có thật', 'Thành phố demo', 'active', 1);
SET @bakery_b_id = LAST_INSERT_ID();

INSERT INTO bakery_members (bakery_id, user_id, member_role) VALUES
  (@bakery_a_id, @owner_a_id, 'owner'),
  (@bakery_b_id, @owner_b_id, 'owner');

-- -----------------------------------------------------------------------------
-- Mẫu bánh + ảnh mẫu
-- -----------------------------------------------------------------------------
INSERT INTO cakes (bakery_id, name, description, flavor, size_label, reference_price, visibility, is_demo) VALUES
  (@bakery_a_id, '[DEMO] Bánh kem hoa tươi', 'Mẫu bánh minh họa.', 'Vani – dâu', '18 cm', 550000.00, 'published', 1);
SET @cake_a_id = LAST_INSERT_ID();

INSERT INTO cakes (bakery_id, name, description, flavor, size_label, reference_price, visibility, is_demo) VALUES
  (@bakery_b_id, '[DEMO] Bánh mousse trà xanh', 'Mẫu bánh minh họa.', 'Trà xanh', '16 cm', 480000.00, 'published', 1);
SET @cake_b_id = LAST_INSERT_ID();

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, cake_id, caption, is_demo) VALUES
  ('cake_photo', 'https://example.com/decakes-demo/cakes/hoa-tuoi.jpg',  'image/jpeg', @owner_a_id, @bakery_a_id, @cake_a_id, '[DEMO] Ảnh mẫu', 1),
  ('cake_photo', 'https://example.com/decakes-demo/cakes/tra-xanh.jpg',  'image/jpeg', @owner_b_id, @bakery_b_id, @cake_b_id, '[DEMO] Ảnh mẫu', 1);

-- -----------------------------------------------------------------------------
-- Yêu cầu của khách (ngày nhận: 7 ngày sau thời điểm chạy script)
-- -----------------------------------------------------------------------------
INSERT INTO design_requests (
  customer_id, inspired_by_cake_id, title, idea_description,
  budget_min, budget_max, size_description, servings, flavor, pickup_at, status, is_demo
) VALUES (
  @customer_id, @cake_a_id,
  '[DEMO] Bánh sinh nhật chủ đề biển',
  'Bánh 2 tầng màu xanh biển, trang trí vỏ sò bằng fondant, ghi chữ "Happy Birthday".',
  500000.00, 800000.00, '2 tầng, đáy 18 cm', 12, 'Vani – chanh dây',
  DATE_ADD(@now, INTERVAL 7 DAY), 'open', 1
);
SET @request_id = LAST_INSERT_ID();

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, design_request_id, caption, is_demo) VALUES
  ('customer_reference', 'https://example.com/decakes-demo/requests/tham-khao-1.jpg', 'image/jpeg',
   @customer_id, @request_id, '[DEMO] Ảnh tham khảo của khách', 1);

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, design_request_id, version_no, caption, is_demo) VALUES
  ('ai_generated', 'https://example.com/decakes-demo/requests/ai-phac-thao-v1.png', 'image/png',
   @customer_id, @request_id, 1, '[DEMO] Phác thảo do AI tạo (minh họa)', 1);

-- Gửi yêu cầu đến 2 tiệm; cả hai đã báo giá.
INSERT INTO request_bakeries (design_request_id, bakery_id, status, sent_at, responded_at) VALUES
  (@request_id, @bakery_a_id, 'quoted', @now, @now),
  (@request_id, @bakery_b_id, 'quoted', @now, @now);

-- Mỗi tiệm gửi một ảnh đề xuất thiết kế (phiên bản 1).
INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, caption, is_demo) VALUES
  ('design_proposal', 'https://example.com/decakes-demo/proposals/tiem-a-v1.jpg', 'image/jpeg',
   @owner_a_id, @bakery_a_id, @request_id, 1, '[DEMO] Đề xuất của Tiệm A', 1);
SET @proposal_a_id = LAST_INSERT_ID();

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, caption, is_demo) VALUES
  ('design_proposal', 'https://example.com/decakes-demo/proposals/tiem-b-v1.jpg', 'image/jpeg',
   @owner_b_id, @bakery_b_id, @request_id, 1, '[DEMO] Đề xuất của Tiệm B', 1);
SET @proposal_b_id = LAST_INSERT_ID();

-- -----------------------------------------------------------------------------
-- Hai báo giá (hạn báo giá: 3 ngày sau thời điểm chạy)
-- -----------------------------------------------------------------------------
INSERT INTO quotes (
  design_request_id, bakery_id, revision_no, price, note,
  estimated_pickup_at, valid_until, confirmed_design_asset_id, created_by_user_id
) VALUES (
  @request_id, @bakery_a_id, 1, 750000.00,
  '[DEMO] Fondant thủ công, cần đặt cọc khi tích hợp thanh toán (chưa có ở v1).',
  DATE_ADD(@now, INTERVAL 7 DAY), DATE_ADD(@now, INTERVAL 3 DAY), @proposal_a_id, @owner_a_id
);
SET @quote_a_id = LAST_INSERT_ID();

INSERT INTO quotes (
  design_request_id, bakery_id, revision_no, price, note,
  estimated_pickup_at, valid_until, confirmed_design_asset_id, created_by_user_id
) VALUES (
  @request_id, @bakery_b_id, 1, 650000.00,
  '[DEMO] Trang trí vỏ sò bằng sô-cô-la trắng thay cho fondant.',
  DATE_ADD(@now, INTERVAL 7 DAY), DATE_ADD(@now, INTERVAL 3 DAY), @proposal_b_id, @owner_b_id
);
SET @quote_b_id = LAST_INSERT_ID();

-- -----------------------------------------------------------------------------
-- Khách chọn báo giá của Tiệm B -> tạo đơn; Tiệm B xác nhận đơn.
-- -----------------------------------------------------------------------------
CALL sp_accept_quote(@quote_b_id, @customer_id, @order_id);
CALL sp_bakery_decide_order(@order_id, @owner_b_id, 'confirm', NULL);

-- -----------------------------------------------------------------------------
-- Xem kết quả
-- -----------------------------------------------------------------------------
SELECT r.id AS request_id, r.title, r.status AS request_status
FROM design_requests r WHERE r.id = @request_id;

SELECT q.id AS quote_id, b.name AS bakery, q.price, q.status AS quote_status, q.valid_until
FROM quotes q JOIN bakeries b ON b.id = q.bakery_id
WHERE q.design_request_id = @request_id
ORDER BY q.id;

SELECT o.id AS order_id, o.quote_id, b.name AS bakery, o.agreed_price, o.status AS order_status,
       o.bakery_decided_at, o.pickup_at
FROM orders o JOIN bakeries b ON b.id = o.bakery_id
WHERE o.id = @order_id;
