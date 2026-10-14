-- =============================================================================
-- DeCakes — install/02_procedures.sql   (thủ tục phiên bản 2)
--
-- Chạy sau install/01_schema.sql (cài mới) hoặc sau migrations/V002 (nâng cấp).
-- File tạo lại thủ tục (DROP PROCEDURE IF EXISTS + CREATE), chạy lại nhiều lần được.
--
-- DBeaver: mở file, Execute SQL Script (Alt+X). Xem database/README.md mục DBeaver
-- nếu DBeaver báo lỗi ở dòng DELIMITER. Thân thủ tục cố ý KHÔNG có dòng trống để
-- tùy chọn "Blank line is statement delimiter" của DBeaver không cắt đôi thủ tục.
--
-- ---------------------------------------------------------------------------
-- QUY ƯỚC CHUNG
-- ---------------------------------------------------------------------------
-- 1. Xác thực: tham số p_actor_user_id / p_customer_id là users.id mà BACKEND lấy từ
--    phiên đăng nhập đã xác thực (claim của cookie/JWT), KHÔNG lấy từ dữ liệu frontend
--    gửi lên. Thủ tục chỉ KIỂM TRA QUYỀN của user đó (trạng thái, thành viên tiệm,
--    chủ yêu cầu); thủ tục không phải cơ chế xác thực.
--
-- 2. Lỗi nghiệp vụ: SIGNAL SQLSTATE '45000', MESSAGE_TEXT = mã ngắn (vd. QUOTE_EXPIRED).
--    Mọi lỗi -> ROLLBACK toàn bộ. Danh sách mã: docs/database-design.md, mục 9.
--
-- 3. Transaction: mỗi thủ tục nghiệp vụ tự START TRANSACTION ... COMMIT. Backend gọi
--    thủ tục NGOÀI transaction của mình (START TRANSACTION trong thủ tục sẽ ngầm
--    COMMIT transaction đang mở).
--
-- 4. Thứ tự khóa thống nhất (tránh deadlock):
--      design_requests -> request_bakeries -> quotes -> orders
--    rồi mới đọc khóa chia sẻ (FOR SHARE) các bảng tham chiếu
--      users, bakeries, bakery_members, media_assets.
--    Mọi lần đọc trong transaction là locking read (FOR UPDATE / FOR SHARE) nên luôn
--    thấy bản commit mới nhất, kể cả khi đã phải chờ khóa.
--
-- 5. Thời gian hiện tại (v_now) được lấy SAU KHI đã giữ đủ khóa, bằng:
--      SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND
--    * NOW()/UTC_TIMESTAMP() trả về thời điểm BẮT ĐẦU câu lệnh. Một câu
--      SELECT ... FOR UPDATE phải chờ khóa vẫn giữ thời điểm trước khi chờ; khi thủ tục
--      được gọi lồng (từ thủ tục/trigger khác) giá trị có thể cố định từ câu lệnh ngoài
--      cùng. Vì vậy không thể dùng riêng NOW()/UTC_TIMESTAMP() để kiểm tra hạn.
--    * SYSDATE() trả về thời điểm hàm thực sự chạy (sau khi đã chờ khóa), nhưng theo
--      time_zone của session. Hiệu NOW(3) - UTC_TIMESTAMP(3) trong CÙNG câu lệnh là
--      độ lệch múi giờ chính xác của session, nên biểu thức trên luôn ra giờ UTC thực,
--      không cần đổi time_zone của server hay session.
--    * Nếu server bật sysdate_is_now (mặc định TẮT; Aiven không bật), SYSDATE() thành
--      NOW(). Khi đó v_now là thời điểm bắt đầu câu lệnh SET v_now — vẫn là sau khi đã
--      giữ khóa, vì câu lệnh đó nằm sau các câu khóa và MySQL đặt lại thời gian cho mỗi
--      câu lệnh của thủ tục được CALL trực tiếp. Điều kiện: gọi trực tiếp bằng CALL,
--      không gọi các thủ tục này từ trigger/function.
--    * Replication: SYSDATE() không tất định, không an toàn với binlog_format=STATEMENT.
--      MySQL 8.4 mặc định ROW (Aiven dùng ROW): binlog ghi giá trị dòng, an toàn. Kể cả
--      với STATEMENT, câu lệnh trong thủ tục được ghi với biến cục bộ đã thay bằng giá trị.
-- =============================================================================

SET NAMES utf8mb4;

USE decakes_db;

DROP PROCEDURE IF EXISTS sp_guard_active_customer;
DROP PROCEDURE IF EXISTS sp_guard_active_bakery_member;
DROP PROCEDURE IF EXISTS sp_guard_bakery_active;
DROP PROCEDURE IF EXISTS sp_guard_quote_design;
DROP PROCEDURE IF EXISTS sp_guard_quote_content;
DROP PROCEDURE IF EXISTS sp_send_request_to_bakery;
DROP PROCEDURE IF EXISTS sp_create_quote;
DROP PROCEDURE IF EXISTS sp_revise_quote;
DROP PROCEDURE IF EXISTS sp_withdraw_quote;
DROP PROCEDURE IF EXISTS sp_accept_quote;
DROP PROCEDURE IF EXISTS sp_bakery_decide_order;

DELIMITER $$

-- =============================================================================
-- THỦ TỤC KIỂM TRA (guard) — gọi bên trong transaction của thủ tục nghiệp vụ.
-- Không tự mở transaction, không có handler: lỗi lan ra thủ tục gọi để ROLLBACK.
-- =============================================================================

-- ----- BLOCK: sp_guard_active_customer -----
CREATE PROCEDURE sp_guard_active_customer(IN p_user_id BIGINT)
BEGIN
  DECLARE v_status VARCHAR(20) DEFAULT NULL;
  DECLARE v_role   VARCHAR(20) DEFAULT NULL;
  IF p_user_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  SELECT status, system_role INTO v_status, v_role
  FROM users WHERE id = p_user_id
  FOR SHARE;
  IF v_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  IF v_status <> 'active' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_ACTIVE';
  END IF;
  IF v_role <> 'customer' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_CUSTOMER';
  END IF;
END$$

-- ----- BLOCK: sp_guard_active_bakery_member -----
-- User phải tồn tại, active, có system_role 'bakery' và là thành viên 'active' của tiệm.
CREATE PROCEDURE sp_guard_active_bakery_member(IN p_bakery_id BIGINT, IN p_user_id BIGINT)
BEGIN
  DECLARE v_user_status   VARCHAR(20) DEFAULT NULL;
  DECLARE v_role          VARCHAR(20) DEFAULT NULL;
  DECLARE v_member_status VARCHAR(20) DEFAULT NULL;
  IF p_bakery_id IS NULL OR p_user_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  SELECT status, system_role INTO v_user_status, v_role
  FROM users WHERE id = p_user_id
  FOR SHARE;
  IF v_user_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_FOUND';
  END IF;
  IF v_user_status <> 'active' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'USER_NOT_ACTIVE';
  END IF;
  SELECT status INTO v_member_status
  FROM bakery_members WHERE bakery_id = p_bakery_id AND user_id = p_user_id
  FOR SHARE;
  IF v_role <> 'bakery' OR v_member_status IS NULL OR v_member_status <> 'active' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_BAKERY_MEMBER';
  END IF;
END$$

-- ----- BLOCK: sp_guard_bakery_active -----
CREATE PROCEDURE sp_guard_bakery_active(IN p_bakery_id BIGINT)
BEGIN
  DECLARE v_status VARCHAR(20) DEFAULT NULL;
  IF p_bakery_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  SELECT profile_status INTO v_status
  FROM bakeries WHERE id = p_bakery_id
  FOR SHARE;
  IF v_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'BAKERY_NOT_FOUND';
  END IF;
  IF v_status <> 'active' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'BAKERY_NOT_ACTIVE';
  END IF;
END$$

-- ----- BLOCK: sp_guard_quote_design -----
-- Quy tắc ảnh thiết kế của một phiên bản báo giá (NULL = không kèm ảnh, hợp lệ):
--   * ảnh phải gắn với CÙNG yêu cầu (loại bỏ cake_photo và ảnh của yêu cầu khác);
--   * design_proposal: phải do CHÍNH tiệm báo giá sở hữu;
--   * customer_reference / ai_generated: ảnh của khách trong yêu cầu, tiệm được dùng
--     làm thiết kế xác nhận ("làm đúng như ảnh này"), không cần thuộc tiệm.
CREATE PROCEDURE sp_guard_quote_design(
  IN p_asset_id   BIGINT,
  IN p_request_id BIGINT,
  IN p_bakery_id  BIGINT
)
guard: BEGIN
  DECLARE v_kind       VARCHAR(30) DEFAULT NULL;
  DECLARE v_request_id BIGINT      DEFAULT NULL;
  DECLARE v_owner      BIGINT      DEFAULT NULL;
  IF p_asset_id IS NULL THEN
    LEAVE guard;
  END IF;
  SELECT asset_kind, design_request_id, owner_bakery_id
    INTO v_kind, v_request_id, v_owner
  FROM media_assets WHERE id = p_asset_id
  FOR SHARE;
  IF v_kind IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DESIGN_ASSET_NOT_FOUND';
  END IF;
  IF v_request_id IS NULL OR v_request_id <> p_request_id
     OR v_kind NOT IN ('design_proposal', 'customer_reference', 'ai_generated') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DESIGN_ASSET_WRONG_REQUEST';
  END IF;
  IF v_kind = 'design_proposal' AND (v_owner IS NULL OR v_owner <> p_bakery_id) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'DESIGN_ASSET_WRONG_BAKERY';
  END IF;
END$$

-- ----- BLOCK: sp_guard_quote_content -----
CREATE PROCEDURE sp_guard_quote_content(
  IN p_price               DECIMAL(12,2),
  IN p_size_description    VARCHAR(100),
  IN p_servings            SMALLINT UNSIGNED,
  IN p_flavor              VARCHAR(200),
  IN p_decoration          TEXT,
  IN p_estimated_pickup_at DATETIME(3),
  IN p_valid_until         DATETIME(3),
  IN p_now                 DATETIME(3)
)
BEGIN
  IF p_price IS NULL OR p_estimated_pickup_at IS NULL OR p_valid_until IS NULL
     OR p_flavor IS NULL OR TRIM(p_flavor) = ''
     OR p_decoration IS NULL OR TRIM(p_decoration) = ''
     OR ((p_size_description IS NULL OR TRIM(p_size_description) = '') AND p_servings IS NULL)
     OR p_servings = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  IF p_price <= 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_INVALID_PRICE';
  END IF;
  IF p_valid_until <= p_now OR p_estimated_pickup_at <= p_now THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_INVALID_TIMES';
  END IF;
END$$

-- =============================================================================
-- THỦ TỤC NGHIỆP VỤ
-- =============================================================================

-- ----- BLOCK: sp_send_request_to_bakery -----
-- Khách gửi yêu cầu (draft/open) cho một tiệm 'active'. Yêu cầu draft chuyển open.
CREATE PROCEDURE sp_send_request_to_bakery(
  IN p_request_id  BIGINT,
  IN p_customer_id BIGINT,
  IN p_bakery_id   BIGINT
)
BEGIN
  DECLARE v_now            DATETIME(3);
  DECLARE v_owner          BIGINT      DEFAULT NULL;
  DECLARE v_request_status VARCHAR(20) DEFAULT NULL;
  DECLARE v_pickup_at      DATETIME(3) DEFAULT NULL;
  DECLARE v_existing       BIGINT      DEFAULT NULL;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_request_id IS NULL OR p_customer_id IS NULL OR p_bakery_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  START TRANSACTION;
  -- Khóa: design_requests -> request_bakeries
  SELECT customer_id, status, pickup_at INTO v_owner, v_request_status, v_pickup_at
  FROM design_requests WHERE id = p_request_id
  FOR UPDATE;
  IF v_owner IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_FOUND';
  END IF;
  SELECT id INTO v_existing
  FROM request_bakeries WHERE design_request_id = p_request_id AND bakery_id = p_bakery_id
  FOR UPDATE;
  -- Bảng tham chiếu (khóa chia sẻ)
  CALL sp_guard_active_customer(p_customer_id);
  IF v_owner <> p_customer_id THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_REQUEST_OWNER';
  END IF;
  IF v_request_status NOT IN ('draft', 'open') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_OPEN';
  END IF;
  IF v_existing IS NOT NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_ALREADY_SENT';
  END IF;
  CALL sp_guard_bakery_active(p_bakery_id);
  -- Thời gian sau khi đã giữ khóa (xem quy ước 5)
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  IF v_pickup_at <= v_now THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_PICKUP_IN_PAST';
  END IF;
  INSERT INTO request_bakeries (design_request_id, bakery_id, status, sent_at, updated_at)
  VALUES (p_request_id, p_bakery_id, 'pending', v_now, v_now);
  IF v_request_status = 'draft' THEN
    UPDATE design_requests SET status = 'open', updated_at = v_now WHERE id = p_request_id;
  END IF;
  COMMIT;
END$$

-- ----- BLOCK: sp_create_quote -----
-- Tiệm gửi báo giá đầu tiên (hoặc báo giá mới sau khi đã rút) cho một yêu cầu.
-- Nếu tiệm đang có phiên bản hiện hành -> QUOTE_ALREADY_EXISTS (dùng sp_revise_quote).
CREATE PROCEDURE sp_create_quote(
  IN  p_request_id          BIGINT,
  IN  p_bakery_id           BIGINT,
  IN  p_actor_user_id       BIGINT,
  IN  p_price               DECIMAL(12,2),
  IN  p_size_description    VARCHAR(100),
  IN  p_servings            SMALLINT UNSIGNED,
  IN  p_flavor              VARCHAR(200),
  IN  p_decoration          TEXT,
  IN  p_inscription         VARCHAR(200),
  IN  p_note                TEXT,
  IN  p_estimated_pickup_at DATETIME(3),
  IN  p_valid_until         DATETIME(3),
  IN  p_design_asset_id     BIGINT,
  OUT p_quote_id            BIGINT
)
BEGIN
  DECLARE v_now            DATETIME(3);
  DECLARE v_request_status VARCHAR(20)  DEFAULT NULL;
  DECLARE v_rb_status      VARCHAR(20)  DEFAULT NULL;
  DECLARE v_current_count  INT          DEFAULT 0;
  DECLARE v_max_revision   INT UNSIGNED DEFAULT NULL;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  SET p_quote_id = NULL;
  IF p_request_id IS NULL OR p_bakery_id IS NULL OR p_actor_user_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  START TRANSACTION;
  -- Khóa: design_requests -> request_bakeries -> quotes
  SELECT status INTO v_request_status
  FROM design_requests WHERE id = p_request_id
  FOR UPDATE;
  IF v_request_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_FOUND';
  END IF;
  SELECT status INTO v_rb_status
  FROM request_bakeries WHERE design_request_id = p_request_id AND bakery_id = p_bakery_id
  FOR UPDATE;
  SELECT COUNT(current_slot), MAX(revision_no) INTO v_current_count, v_max_revision
  FROM quotes WHERE design_request_id = p_request_id AND bakery_id = p_bakery_id
  FOR UPDATE;
  -- Bảng tham chiếu
  CALL sp_guard_active_bakery_member(p_bakery_id, p_actor_user_id);
  IF v_rb_status IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_SENT_TO_BAKERY';
  END IF;
  IF v_request_status <> 'open' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_OPEN';
  END IF;
  IF v_rb_status = 'declined' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_DECLINED_BY_BAKERY';
  END IF;
  IF v_current_count > 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_ALREADY_EXISTS';
  END IF;
  CALL sp_guard_bakery_active(p_bakery_id);
  CALL sp_guard_quote_design(p_design_asset_id, p_request_id, p_bakery_id);
  -- Thời gian sau khi đã giữ khóa (xem quy ước 5)
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  CALL sp_guard_quote_content(p_price, p_size_description, p_servings, p_flavor, p_decoration,
                              p_estimated_pickup_at, p_valid_until, v_now);
  INSERT INTO quotes (
    design_request_id, bakery_id, revision_no, price,
    size_description, servings, flavor, decoration_description, inscription_text, note,
    estimated_pickup_at, valid_until, confirmed_design_asset_id, created_by_user_id,
    status, created_at, updated_at
  ) VALUES (
    p_request_id, p_bakery_id, COALESCE(v_max_revision, 0) + 1, p_price,
    NULLIF(TRIM(p_size_description), ''), p_servings, TRIM(p_flavor), TRIM(p_decoration),
    NULLIF(TRIM(p_inscription), ''), p_note,
    p_estimated_pickup_at, p_valid_until, p_design_asset_id, p_actor_user_id,
    'submitted', v_now, v_now
  );
  SET p_quote_id = LAST_INSERT_ID();
  UPDATE request_bakeries
     SET status = 'quoted', responded_at = COALESCE(responded_at, v_now), updated_at = v_now
   WHERE design_request_id = p_request_id AND bakery_id = p_bakery_id;
  COMMIT;
END$$

-- ----- BLOCK: sp_revise_quote -----
-- Tiệm sửa báo giá: phiên bản p_quote_id (phải là phiên bản hiện hành 'submitted')
-- chuyển 'superseded', tạo phiên bản mới revision_no + 1 với toàn bộ nội dung mới.
-- Hai lần sửa đồng thời cùng nhắm p_quote_id: lần sau chờ khóa yêu cầu, rồi thấy
-- p_quote_id đã 'superseded' -> QUOTE_NOT_CURRENT (không mất cập nhật, không có hai
-- phiên bản hiện hành). UNIQUE uq_quotes_current là lớp chặn cuối.
CREATE PROCEDURE sp_revise_quote(
  IN  p_quote_id            BIGINT,
  IN  p_actor_user_id       BIGINT,
  IN  p_price               DECIMAL(12,2),
  IN  p_size_description    VARCHAR(100),
  IN  p_servings            SMALLINT UNSIGNED,
  IN  p_flavor              VARCHAR(200),
  IN  p_decoration          TEXT,
  IN  p_inscription         VARCHAR(200),
  IN  p_note                TEXT,
  IN  p_estimated_pickup_at DATETIME(3),
  IN  p_valid_until         DATETIME(3),
  IN  p_design_asset_id     BIGINT,
  OUT p_new_quote_id        BIGINT
)
BEGIN
  DECLARE v_now            DATETIME(3);
  DECLARE v_request_id     BIGINT       DEFAULT NULL;
  DECLARE v_bakery_id      BIGINT       DEFAULT NULL;
  DECLARE v_request_status VARCHAR(20)  DEFAULT NULL;
  DECLARE v_quote_status   VARCHAR(20)  DEFAULT NULL;
  DECLARE v_max_revision   INT UNSIGNED DEFAULT NULL;
  DECLARE v_rb_id          BIGINT       DEFAULT NULL;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  SET p_new_quote_id = NULL;
  IF p_quote_id IS NULL OR p_actor_user_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  -- Tra yêu cầu + tiệm của báo giá (hai cột này không bao giờ đổi), ngoài transaction.
  SELECT design_request_id, bakery_id INTO v_request_id, v_bakery_id
  FROM quotes WHERE id = p_quote_id;
  IF v_request_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_FOUND';
  END IF;
  START TRANSACTION;
  -- Khóa: design_requests -> request_bakeries -> quotes
  SELECT status INTO v_request_status
  FROM design_requests WHERE id = v_request_id
  FOR UPDATE;
  SELECT id INTO v_rb_id
  FROM request_bakeries WHERE design_request_id = v_request_id AND bakery_id = v_bakery_id
  FOR UPDATE;
  SELECT MAX(revision_no) INTO v_max_revision
  FROM quotes WHERE design_request_id = v_request_id AND bakery_id = v_bakery_id
  FOR UPDATE;
  SELECT status INTO v_quote_status
  FROM quotes WHERE id = p_quote_id
  FOR UPDATE;
  -- Bảng tham chiếu
  CALL sp_guard_active_bakery_member(v_bakery_id, p_actor_user_id);
  IF v_quote_status = 'accepted' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_ALREADY_ACCEPTED';
  END IF;
  IF v_request_status <> 'open' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_OPEN';
  END IF;
  IF v_quote_status <> 'submitted' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_CURRENT';
  END IF;
  CALL sp_guard_bakery_active(v_bakery_id);
  CALL sp_guard_quote_design(p_design_asset_id, v_request_id, v_bakery_id);
  -- Thời gian sau khi đã giữ khóa (xem quy ước 5)
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  CALL sp_guard_quote_content(p_price, p_size_description, p_servings, p_flavor, p_decoration,
                              p_estimated_pickup_at, p_valid_until, v_now);
  -- Hạ phiên bản cũ TRƯỚC khi thêm phiên bản mới (uq_quotes_current).
  UPDATE quotes SET status = 'superseded', updated_at = v_now WHERE id = p_quote_id;
  INSERT INTO quotes (
    design_request_id, bakery_id, revision_no, price,
    size_description, servings, flavor, decoration_description, inscription_text, note,
    estimated_pickup_at, valid_until, confirmed_design_asset_id, created_by_user_id,
    status, created_at, updated_at
  ) VALUES (
    v_request_id, v_bakery_id, v_max_revision + 1, p_price,
    NULLIF(TRIM(p_size_description), ''), p_servings, TRIM(p_flavor), TRIM(p_decoration),
    NULLIF(TRIM(p_inscription), ''), p_note,
    p_estimated_pickup_at, p_valid_until, p_design_asset_id, p_actor_user_id,
    'submitted', v_now, v_now
  );
  SET p_new_quote_id = LAST_INSERT_ID();
  COMMIT;
END$$

-- ----- BLOCK: sp_withdraw_quote -----
-- Tiệm rút phiên bản hiện hành. Được phép cả khi tiệm không còn 'active'
-- (rút lại không tạo cam kết mới). Thành viên phải còn 'active'.
CREATE PROCEDURE sp_withdraw_quote(IN p_quote_id BIGINT, IN p_actor_user_id BIGINT)
BEGIN
  DECLARE v_now          DATETIME(3);
  DECLARE v_request_id   BIGINT      DEFAULT NULL;
  DECLARE v_bakery_id    BIGINT      DEFAULT NULL;
  DECLARE v_req_status   VARCHAR(20) DEFAULT NULL;
  DECLARE v_quote_status VARCHAR(20) DEFAULT NULL;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_quote_id IS NULL OR p_actor_user_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  SELECT design_request_id, bakery_id INTO v_request_id, v_bakery_id
  FROM quotes WHERE id = p_quote_id;
  IF v_request_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_FOUND';
  END IF;
  START TRANSACTION;
  SELECT status INTO v_req_status
  FROM design_requests WHERE id = v_request_id
  FOR UPDATE;
  SELECT status INTO v_quote_status
  FROM quotes WHERE id = p_quote_id
  FOR UPDATE;
  CALL sp_guard_active_bakery_member(v_bakery_id, p_actor_user_id);
  IF v_quote_status = 'accepted' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_ALREADY_ACCEPTED';
  END IF;
  IF v_quote_status <> 'submitted' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_CURRENT';
  END IF;
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  UPDATE quotes
     SET status = 'withdrawn', withdrawn_at = v_now, updated_at = v_now
   WHERE id = p_quote_id;
  COMMIT;
END$$

-- ----- BLOCK: sp_accept_quote -----
-- Khách chọn báo giá -> tạo đơn với BẢN CHỐT, trong một transaction.
--   1. Khóa design_requests rồi quotes (FOR UPDATE). Mọi lần chọn cho cùng yêu cầu
--      xếp hàng tại dòng yêu cầu.
--   2. Đọc khóa chia sẻ user, tiệm, ảnh thiết kế (bản mới nhất).
--   3. Lấy v_now SAU khi giữ đủ khóa; kiểm tra hạn với v_now.
--   4. Kiểm tra: chủ yêu cầu, user active, yêu cầu open, báo giá submitted, tiệm active,
--      ảnh thiết kế hợp lệ, chưa hết hạn.
--   5. INSERT orders (sao chép nội dung phiên bản + ảnh), cập nhật trạng thái, COMMIT.
-- Lần chọn thứ hai sau khi chờ khóa thấy yêu cầu 'ordered' -> REQUEST_NOT_OPEN.
-- UNIQUE(orders.quote_id) và UNIQUE(orders.design_request_id) là lớp chặn cuối.
CREATE PROCEDURE sp_accept_quote(
  IN  p_quote_id    BIGINT,
  IN  p_customer_id BIGINT,
  OUT p_order_id    BIGINT
)
BEGIN
  DECLARE v_now              DATETIME(3);
  DECLARE v_request_id       BIGINT;
  DECLARE v_request_customer BIGINT;
  DECLARE v_request_status   VARCHAR(20);
  DECLARE v_bakery_id        BIGINT;
  DECLARE v_quote_status     VARCHAR(20);
  DECLARE v_revision_no      INT UNSIGNED;
  DECLARE v_valid_until      DATETIME(3);
  DECLARE v_price            DECIMAL(12,2);
  DECLARE v_currency         CHAR(3);
  DECLARE v_pickup_at        DATETIME(3);
  DECLARE v_size             VARCHAR(100);
  DECLARE v_servings         SMALLINT UNSIGNED;
  DECLARE v_flavor           VARCHAR(200);
  DECLARE v_decoration       TEXT;
  DECLARE v_inscription      VARCHAR(200);
  DECLARE v_note             TEXT;
  DECLARE v_asset_id         BIGINT;
  DECLARE v_asset_kind       VARCHAR(30)   DEFAULT NULL;
  DECLARE v_asset_version    INT UNSIGNED  DEFAULT NULL;
  DECLARE v_asset_url        VARCHAR(1024) DEFAULT NULL;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  SET p_order_id = NULL;
  IF p_quote_id IS NULL OR p_customer_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  -- Tra yêu cầu của báo giá (không đổi), ngoài transaction, không khóa.
  SELECT design_request_id INTO v_request_id
  FROM quotes WHERE id = p_quote_id;
  IF v_request_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_FOUND';
  END IF;
  START TRANSACTION;
  -- (1) Khóa yêu cầu, rồi báo giá.
  SELECT customer_id, status INTO v_request_customer, v_request_status
  FROM design_requests WHERE id = v_request_id
  FOR UPDATE;
  SELECT bakery_id, status, revision_no, valid_until, price, currency, estimated_pickup_at,
         size_description, servings, flavor, decoration_description, inscription_text, note,
         confirmed_design_asset_id
    INTO v_bakery_id, v_quote_status, v_revision_no, v_valid_until, v_price, v_currency, v_pickup_at,
         v_size, v_servings, v_flavor, v_decoration, v_inscription, v_note,
         v_asset_id
  FROM quotes WHERE id = p_quote_id
  FOR UPDATE;
  -- (2) Bảng tham chiếu (khóa chia sẻ).
  CALL sp_guard_active_customer(p_customer_id);
  IF v_request_customer <> p_customer_id THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_REQUEST_OWNER';
  END IF;
  IF v_request_status <> 'open' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_OPEN';
  END IF;
  IF v_quote_status <> 'submitted' THEN
    -- withdrawn, superseded, accepted, not_selected
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_AVAILABLE';
  END IF;
  CALL sp_guard_bakery_active(v_bakery_id);
  -- Kiểm tra lại ảnh thiết kế trước khi chốt (dữ liệu ảnh có thể đã bị sửa trực tiếp).
  CALL sp_guard_quote_design(v_asset_id, v_request_id, v_bakery_id);
  IF v_asset_id IS NOT NULL THEN
    SELECT asset_kind, version_no, storage_url INTO v_asset_kind, v_asset_version, v_asset_url
    FROM media_assets WHERE id = v_asset_id
    FOR SHARE;
  END IF;
  -- (3) Thời gian thực SAU khi giữ đủ khóa (xem quy ước 5).
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  IF v_valid_until <= v_now THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_EXPIRED';
  END IF;
  -- (5) Tạo đơn với bản chốt.
  INSERT INTO orders (
    quote_id, design_request_id, customer_id, bakery_id,
    agreed_quote_revision_no, agreed_price, currency, agreed_pickup_at,
    agreed_size_description, agreed_servings, agreed_flavor,
    agreed_decoration_description, agreed_inscription_text, agreed_note,
    agreed_design_asset_id, agreed_design_asset_kind, agreed_design_version_no, agreed_design_url,
    status, created_at, updated_at
  ) VALUES (
    p_quote_id, v_request_id, p_customer_id, v_bakery_id,
    v_revision_no, v_price, v_currency, v_pickup_at,
    v_size, v_servings, v_flavor,
    v_decoration, v_inscription, v_note,
    v_asset_id, v_asset_kind, v_asset_version, v_asset_url,
    'pending_confirmation', v_now, v_now
  );
  SET p_order_id = LAST_INSERT_ID();
  UPDATE quotes SET status = 'accepted', updated_at = v_now WHERE id = p_quote_id;
  UPDATE quotes
     SET status = 'not_selected', updated_at = v_now
   WHERE design_request_id = v_request_id AND id <> p_quote_id AND status = 'submitted';
  UPDATE design_requests SET status = 'ordered', updated_at = v_now WHERE id = v_request_id;
  COMMIT;
END$$

-- ----- BLOCK: sp_bakery_decide_order -----
-- Thành viên 'active' của đúng tiệm xác nhận ('confirm') hoặc từ chối ('reject') đơn
-- đang 'pending_confirmation'.
--   * confirm: tiệm phải 'active'.
--   * reject : được phép cả khi tiệm không active (giải phóng khách). Yêu cầu chuyển
--              'cancelled' (giả định v1 giữ nguyên: khách tạo yêu cầu mới).
CREATE PROCEDURE sp_bakery_decide_order(
  IN p_order_id      BIGINT,
  IN p_actor_user_id BIGINT,
  IN p_decision      VARCHAR(10),
  IN p_reason        VARCHAR(500)
)
BEGIN
  DECLARE v_now        DATETIME(3);
  DECLARE v_request_id BIGINT      DEFAULT NULL;
  DECLARE v_bakery_id  BIGINT      DEFAULT NULL;
  DECLARE v_status     VARCHAR(30) DEFAULT NULL;
  DECLARE v_req_status VARCHAR(20) DEFAULT NULL;
  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;
  IF p_order_id IS NULL OR p_actor_user_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  IF p_decision IS NULL OR p_decision NOT IN ('confirm', 'reject') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DECISION';
  END IF;
  SELECT design_request_id INTO v_request_id
  FROM orders WHERE id = p_order_id;
  IF v_request_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_NOT_FOUND';
  END IF;
  START TRANSACTION;
  -- Khóa: design_requests -> orders
  SELECT status INTO v_req_status
  FROM design_requests WHERE id = v_request_id
  FOR UPDATE;
  SELECT bakery_id, status INTO v_bakery_id, v_status
  FROM orders WHERE id = p_order_id
  FOR UPDATE;
  CALL sp_guard_active_bakery_member(v_bakery_id, p_actor_user_id);
  IF v_status <> 'pending_confirmation' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_NOT_PENDING';
  END IF;
  IF p_decision = 'confirm' THEN
    CALL sp_guard_bakery_active(v_bakery_id);
  END IF;
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  IF p_decision = 'confirm' THEN
    UPDATE orders
       SET status = 'confirmed', bakery_decided_at = v_now,
           bakery_decided_by_user_id = p_actor_user_id, updated_at = v_now
     WHERE id = p_order_id;
  ELSE
    UPDATE orders
       SET status = 'rejected_by_bakery', bakery_decided_at = v_now,
           bakery_decided_by_user_id = p_actor_user_id, rejection_reason = p_reason,
           updated_at = v_now
     WHERE id = p_order_id;
    UPDATE design_requests SET status = 'cancelled', updated_at = v_now WHERE id = v_request_id;
  END IF;
  COMMIT;
END$$

DELIMITER ;
