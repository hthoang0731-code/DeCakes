-- =============================================================================
-- DeCakes — migrations/V006__order_decision_time_rules.sql   (ĐỀ XUẤT, chưa áp dụng)
--
-- Quy tắc Orders đã duyệt 01/11/2026 cho chọn báo giá và quyết định đơn. Chạy sau V005.
-- KHÔNG tự chạy lên Aiven; chỉ đã nạp trên MySQL 8.4 kiểm thử dùng một lần.
-- Không có file .grants.sql: chỉ tạo lại procedure (SQL SECURITY DEFINER mặc định), backend đã có
-- EXECUTE trên decakes_db.*; không cần và không thêm quyền nào.
--
-- Thay đổi (chỉ tạo lại 2 procedure; không đổi bảng, cột, quyền hay dữ liệu):
--   1. sp_accept_quote (bản V005):
--      * Giữ nguyên thứ tự khóa, các guard, quyền gói, bản chốt INSERT orders và transaction.
--      * V005 lấy v_now TRƯỚC sp_guard_bakery_entitled, mà guard đọc FOR SHARE nên có thể phải chờ khóa;
--        khi đó hạn báo giá bị so với giờ trước lúc chờ. V006 lấy lại v_now SAU guard, tức sau mọi lần
--        đọc có khóa, và dùng giá trị đó cho QUOTE_EXPIRED, QUOTE_PICKUP_PASSED và created_at của đơn.
--        Quyền gói vẫn được đánh giá tại v_now lần đầu (sau khi giữ khóa nghiệp vụ) như V005.
--      * Mới: giờ nhận của phiên bản báo giá phải còn ở tương lai (QUOTE_PICKUP_PASSED), áp dụng cả cho
--        báo giá cũ tạo trước quy tắc validUntil <= estimatedPickupAt.
--   2. sp_bakery_decide_order (bản install/02):
--      * Giữ nguyên quyền thành viên, thứ tự khóa design_requests -> orders, confirm cần tiệm active,
--        reject được cả khi tiệm không active, không kiểm tra gói.
--      * Mới: reject bắt buộc p_reason không rỗng sau TRIM (ORDER_REJECTION_REASON_REQUIRED), kiểm tra
--        trước khi mở transaction.
--      * Mới: confirm bị chặn khi v_now >= agreed_pickup_at (ORDER_PICKUP_PASSED); v_now lấy sau khi giữ
--        đủ khóa. Reject sau giờ nhận vẫn được.
--
-- VẬN HÀNH: DROP/CREATE PROCEDURE là DDL, tự COMMIT ngầm từng câu; migration KHÔNG atomic. Nếu lỗi giữa
-- chừng (vd. sau DROP, trước CREATE) thì procedure có thể đang thiếu và version 6 chưa được ghi; cần kiểm
-- tra thủ công trạng thái procedure trước khi xử lý tiếp, không coi là chạy lại tự động an toàn. Dòng
-- version 6 chỉ được ghi ở câu cuối, sau khi mọi câu trước đã thành công. API chặn accept/decide (503
-- ORDER_WORKFLOW_NOT_READY) cho tới khi có version 6.
-- =============================================================================

SET NAMES utf8mb4;

USE decakes_db;

DROP PROCEDURE IF EXISTS tmp_v006_precheck;

DELIMITER $$

CREATE PROCEDURE tmp_v006_precheck()
BEGIN
  DECLARE v_v5 INT DEFAULT 0;
  DECLARE v_v6 INT DEFAULT 0;
  SELECT COUNT(*) INTO v_v5 FROM schema_migrations WHERE version = 5;
  SELECT COUNT(*) INTO v_v6 FROM schema_migrations WHERE version = 6;
  IF v_v5 <> 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V006_REQUIRES_V5';
  END IF;
  IF v_v6 <> 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V006_ALREADY_APPLIED';
  END IF;
END$$

DELIMITER ;

CALL tmp_v006_precheck();

DROP PROCEDURE tmp_v006_precheck;

DELIMITER $$

DROP PROCEDURE IF EXISTS sp_accept_quote$$

-- ----- BLOCK: sp_accept_quote -----
-- Khách chọn báo giá -> tạo đơn với BẢN CHỐT, trong một transaction (bản V006).
--   1. Khóa design_requests rồi quotes (FOR UPDATE). Mọi lần chọn cho cùng yêu cầu
--      xếp hàng tại dòng yêu cầu.
--   2. Đọc khóa chia sẻ user, tiệm, ảnh thiết kế (bản mới nhất), kỳ sử dụng của tiệm (guard gói).
--   3. Lấy v_now SAU mọi lần đọc có khóa; kiểm tra hạn báo giá và giờ nhận với v_now.
--   4. Kiểm tra: chủ yêu cầu, user active, yêu cầu open, báo giá submitted, tiệm active,
--      ảnh thiết kế hợp lệ, gói còn hạn, chưa hết hạn, giờ nhận còn ở tương lai.
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
  -- (3) Thời gian thực SAU khi giữ đủ khóa (xem quy ước 5 của install/02).
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  -- V005: quyền gói của tiệm tại v_now (sau khi đã giữ khóa nghiệp vụ).
  CALL sp_guard_bakery_entitled(v_bakery_id, v_now);
  -- V006: guard gói đọc FOR SHARE và có thể đã chờ khóa; lấy lại giờ sau mọi lần đọc có khóa.
  SET v_now = SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND;
  IF v_valid_until <= v_now THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_EXPIRED';
  END IF;
  -- V006: đơn không được chốt với giờ nhận đã qua (kể cả báo giá cũ có valid_until sau giờ nhận).
  IF v_pickup_at <= v_now THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_PICKUP_PASSED';
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

DROP PROCEDURE IF EXISTS sp_bakery_decide_order$$

-- ----- BLOCK: sp_bakery_decide_order -----
-- Thành viên 'active' của đúng tiệm xác nhận ('confirm') hoặc từ chối ('reject') đơn
-- đang 'pending_confirmation' (bản V006).
--   * confirm: tiệm phải 'active' và giờ hiện tại (sau khi giữ khóa) phải trước agreed_pickup_at.
--   * reject : bắt buộc lý do không rỗng; được phép cả khi tiệm không active hoặc đã qua giờ nhận
--              (giải phóng khách). Yêu cầu chuyển 'cancelled' (khách tạo yêu cầu mới).
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
  DECLARE v_pickup_at  DATETIME(3) DEFAULT NULL;
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
  -- V006: từ chối phải có lý do.
  IF p_decision = 'reject' AND (p_reason IS NULL OR TRIM(p_reason) = '') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_REJECTION_REASON_REQUIRED';
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
  SELECT bakery_id, status, agreed_pickup_at INTO v_bakery_id, v_status, v_pickup_at
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
    -- V006: không xác nhận đơn khi đã tới giờ nhận.
    IF v_now >= v_pickup_at THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_PICKUP_PASSED';
    END IF;
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

INSERT INTO schema_migrations (version, description)
VALUES (6, 'order rules: pickup must be in the future at accept/confirm, rejection reason required');
