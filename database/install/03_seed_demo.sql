-- =============================================================================
-- DeCakes — install/03_seed_demo.sql   (dữ liệu demo, phiên bản 2)
--
-- DỮ LIỆU DEMO HƯ CẤU. Mọi tiệm, người dùng, yêu cầu, báo giá và đơn dưới đây chỉ để
-- trình diễn luồng nghiệp vụ, KHÔNG phải đối tác hay giao dịch có thật.
--
-- Cách đánh dấu:
--   * is_demo = 1 trên users, bakeries, cakes, design_requests, media_assets.
--   * Tên/tiêu đề có tiền tố "[DEMO]".
--   * Email đuôi .invalid (RFC 2606, không thể tồn tại).
--   * URL ảnh trỏ tới example.com (tên miền dành cho ví dụ).
--   * password_hash = NULL: tài khoản demo không đăng nhập được; DB không chứa mật khẩu.
--
-- Kịch bản (đi qua stored procedure như ứng dụng thật):
--   khách tạo yêu cầu -> gửi cho Tiệm A và Tiệm B -> mỗi tiệm báo giá ->
--   Tiệm B sửa báo giá (phiên bản 2) -> khách chọn báo giá của Tiệm B -> Tiệm B xác nhận.
--
-- Chạy sau install/01 + 02, MỘT LẦN trên database mới (chạy lại sẽ lỗi trùng email).
-- Thời gian tính từ lúc chạy script nên dữ liệu luôn "còn hạn" khi vừa tạo.
-- =============================================================================

SET NAMES utf8mb4;
SET SQL_SAFE_UPDATES = 0;

USE decakes_db;

SET @now = UTC_TIMESTAMP(3);

-- -----------------------------------------------------------------------------
-- Người dùng
-- -----------------------------------------------------------------------------
INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('khach.demo@demo.decakes.invalid', NULL, '[DEMO] Khách hàng mẫu', 'customer', 1);
SET @customer_id = LAST_INSERT_ID();

INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('chu.tiem.a@demo.decakes.invalid', NULL, '[DEMO] Chủ Tiệm A', 'bakery', 1);
SET @owner_a_id = LAST_INSERT_ID();

INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('chu.tiem.b@demo.decakes.invalid', NULL, '[DEMO] Chủ Tiệm B', 'bakery', 1);
SET @owner_b_id = LAST_INSERT_ID();

INSERT INTO users (email, password_hash, full_name, system_role, is_demo) VALUES
  ('admin.demo@demo.decakes.invalid', NULL, '[DEMO] Quản trị viên', 'admin', 1);

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
  ('cake_photo', 'https://example.com/decakes-demo/cakes/hoa-tuoi.jpg', 'image/jpeg', @owner_a_id, @bakery_a_id, @cake_a_id, '[DEMO] Ảnh mẫu', 1),
  ('cake_photo', 'https://example.com/decakes-demo/cakes/tra-xanh.jpg', 'image/jpeg', @owner_b_id, @bakery_b_id, @cake_b_id, '[DEMO] Ảnh mẫu', 1);

-- -----------------------------------------------------------------------------
-- Yêu cầu của khách (draft) + ảnh của khách
-- -----------------------------------------------------------------------------
INSERT INTO design_requests (
  customer_id, inspired_by_cake_id, title, idea_description,
  budget_min, budget_max, size_description, servings, flavor, inscription_text,
  pickup_at, status, is_demo
) VALUES (
  @customer_id, @cake_a_id,
  '[DEMO] Bánh sinh nhật chủ đề biển',
  'Bánh 2 tầng màu xanh biển, trang trí vỏ sò.',
  500000.00, 800000.00, '2 tầng, đáy 18 cm', 12, 'Vani – chanh dây', 'Happy Birthday',
  DATE_ADD(@now, INTERVAL 7 DAY), 'draft', 1
);
SET @request_id = LAST_INSERT_ID();

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, design_request_id, caption, is_demo) VALUES
  ('customer_reference', 'https://example.com/decakes-demo/requests/tham-khao-1.jpg', 'image/jpeg',
   @customer_id, @request_id, '[DEMO] Ảnh tham khảo của khách', 1);
SET @customer_ref_id = LAST_INSERT_ID();

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, design_request_id, version_no, caption, is_demo) VALUES
  ('ai_generated', 'https://example.com/decakes-demo/requests/ai-phac-thao-v1.png', 'image/png',
   @customer_id, @request_id, 1, '[DEMO] Phác thảo do AI tạo (minh họa)', 1);

-- Gửi yêu cầu cho hai tiệm (yêu cầu draft -> open).
CALL sp_send_request_to_bakery(@request_id, @customer_id, @bakery_a_id);
CALL sp_send_request_to_bakery(@request_id, @customer_id, @bakery_b_id);

-- Ảnh đề xuất của từng tiệm (ảnh bất biến; mỗi phiên bản thiết kế là một dòng).
INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, caption, is_demo) VALUES
  ('design_proposal', 'https://example.com/decakes-demo/proposals/tiem-a-v1.jpg', 'image/jpeg',
   @owner_a_id, @bakery_a_id, @request_id, 1, '[DEMO] Đề xuất của Tiệm A – phiên bản 1', 1);
SET @proposal_a1_id = LAST_INSERT_ID();

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, caption, is_demo) VALUES
  ('design_proposal', 'https://example.com/decakes-demo/proposals/tiem-b-v1.jpg', 'image/jpeg',
   @owner_b_id, @bakery_b_id, @request_id, 1, '[DEMO] Đề xuất của Tiệm B – phiên bản 1', 1);
SET @proposal_b1_id = LAST_INSERT_ID();

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, caption, is_demo) VALUES
  ('design_proposal', 'https://example.com/decakes-demo/proposals/tiem-b-v2.jpg', 'image/jpeg',
   @owner_b_id, @bakery_b_id, @request_id, 2, '[DEMO] Đề xuất của Tiệm B – phiên bản 2', 1);
SET @proposal_b2_id = LAST_INSERT_ID();

-- -----------------------------------------------------------------------------
-- Báo giá (hạn: 3 ngày; ngày nhận: 7 ngày sau lúc chạy script)
-- -----------------------------------------------------------------------------
SET @pickup = DATE_ADD(@now, INTERVAL 7 DAY);
SET @valid  = DATE_ADD(@now, INTERVAL 3 DAY);

CALL sp_create_quote(
  @request_id, @bakery_a_id, @owner_a_id,
  750000.00, '2 tầng, đáy 18 cm', 12, 'Vani – chanh dây',
  '[DEMO] Phủ kem xanh biển, vỏ sò fondant thủ công.', 'Happy Birthday',
  '[DEMO] Ghi chú báo giá Tiệm A.',
  @pickup, @valid, @proposal_a1_id, @quote_a_id);

CALL sp_create_quote(
  @request_id, @bakery_b_id, @owner_b_id,
  650000.00, '2 tầng, đáy 18 cm', 12, 'Vani – chanh dây',
  '[DEMO] Vỏ sò bằng sô-cô-la trắng.', 'Happy Birthday',
  '[DEMO] Ghi chú báo giá Tiệm B, phiên bản 1.',
  @pickup, @valid, @proposal_b1_id, @quote_b1_id);

-- Tiệm B sửa báo giá: phiên bản 1 -> superseded, phiên bản 2 là hiện hành.
CALL sp_revise_quote(
  @quote_b1_id, @owner_b_id,
  680000.00, '2 tầng, đáy 18 cm', 12, 'Vani – chanh dây',
  '[DEMO] Vỏ sò sô-cô-la trắng, thêm ngọc trai đường.', 'Happy Birthday Mai',
  '[DEMO] Phiên bản 2: theo góp ý của khách, đổi chữ và thêm trang trí.',
  @pickup, @valid, @proposal_b2_id, @quote_b2_id);

-- -----------------------------------------------------------------------------
-- Khách chọn báo giá Tiệm B (phiên bản 2) -> tạo đơn; Tiệm B xác nhận.
-- -----------------------------------------------------------------------------
CALL sp_accept_quote(@quote_b2_id, @customer_id, @order_id);
CALL sp_bakery_decide_order(@order_id, @owner_b_id, 'confirm', NULL);

-- -----------------------------------------------------------------------------
-- Xem kết quả
-- -----------------------------------------------------------------------------
SELECT r.id AS request_id, r.title, r.status AS request_status
FROM design_requests r WHERE r.id = @request_id;

SELECT q.id AS quote_id, b.name AS bakery, q.revision_no, q.price, q.status AS quote_status, q.valid_until
FROM quotes q JOIN bakeries b ON b.id = q.bakery_id
WHERE q.design_request_id = @request_id
ORDER BY q.bakery_id, q.revision_no;

SELECT o.id AS order_id, b.name AS bakery, o.agreed_quote_revision_no, o.agreed_price,
       o.agreed_flavor, o.agreed_inscription_text, o.agreed_design_url,
       o.status AS order_status, o.bakery_decided_at, o.agreed_pickup_at
FROM orders o JOIN bakeries b ON b.id = o.bakery_id
WHERE o.id = @order_id;
