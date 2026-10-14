-- FIXTURE KIỂM THỬ v1 (commit a9a238e) — chỉ dùng để kiểm thử migration V002.
-- =============================================================================
-- DeCakes — 02_procedures.sql
-- Thủ tục cho hai thao tác cần tính toàn vẹn cao:
--   sp_accept_quote        : khách chọn báo giá -> tạo đơn (một transaction)
--   sp_bakery_decide_order : tiệm xác nhận hoặc từ chối đơn
--
-- Lỗi nghiệp vụ được báo bằng SQLSTATE '45000' với MESSAGE_TEXT là mã ngắn
-- (vd. 'QUOTE_EXPIRED'), để ứng dụng ASP.NET Core bắt MySqlException và hiển thị
-- thông báo phù hợp. Mọi lỗi đều ROLLBACK toàn bộ transaction.
--
-- Chạy sau 01_schema.sql. File dùng DELIMITER nên chạy bằng mysql CLI hoặc
-- MySQL Workbench (không dùng được qua driver gửi từng câu lệnh).
-- =============================================================================

SET NAMES utf8mb4;
USE decakes_db;

DROP PROCEDURE IF EXISTS sp_accept_quote;
DROP PROCEDURE IF EXISTS sp_bakery_decide_order;

DELIMITER $$

-- -----------------------------------------------------------------------------
-- sp_accept_quote
--   p_quote_id     : báo giá khách chọn
--   p_customer_id  : khách đang đăng nhập (ứng dụng truyền vào, không lấy từ form)
--   p_order_id OUT : id đơn vừa tạo
--
-- Chống tạo nhiều đơn:
--   1. Khóa dòng design_requests (FOR UPDATE) TRƯỚC, rồi mới khóa báo giá. Mọi lần chọn
--      cho cùng yêu cầu phải xếp hàng tại dòng này; thứ tự khóa cố định tránh deadlock.
--   2. Sau khi có khóa, đọc lại trạng thái mới nhất (locking read) và kiểm tra:
--      chủ yêu cầu, yêu cầu còn 'open', báo giá còn 'submitted', chưa hết hạn.
--   3. Lần chọn thứ hai (đồng thời hoặc bấm lại) sẽ thấy yêu cầu đã 'ordered' -> lỗi.
--   4. UNIQUE(orders.quote_id) và UNIQUE(orders.design_request_id) là lớp chặn cuối,
--      kể cả khi có code nào đó INSERT thẳng vào orders mà không qua thủ tục này.
-- -----------------------------------------------------------------------------
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
  DECLARE v_valid_until      DATETIME(3);
  DECLARE v_price            DECIMAL(12,2);
  DECLARE v_currency         CHAR(3);
  DECLARE v_pickup_at        DATETIME(3);

  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  SET p_order_id = NULL;
  SET v_now = UTC_TIMESTAMP(3);

  START TRANSACTION;

  -- Tìm yêu cầu của báo giá (chưa khóa; design_request_id của báo giá không bao giờ đổi).
  SELECT design_request_id INTO v_request_id
  FROM quotes
  WHERE id = p_quote_id;

  IF v_request_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_FOUND';
  END IF;

  -- (1) Khóa yêu cầu.
  SELECT customer_id, status
    INTO v_request_customer, v_request_status
  FROM design_requests
  WHERE id = v_request_id
  FOR UPDATE;

  IF v_request_customer <> p_customer_id THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_REQUEST_OWNER';
  END IF;

  IF v_request_status <> 'open' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'REQUEST_NOT_OPEN';
  END IF;

  -- (2) Khóa báo giá.
  SELECT bakery_id, status, valid_until, price, currency, estimated_pickup_at
    INTO v_bakery_id, v_quote_status, v_valid_until, v_price, v_currency, v_pickup_at
  FROM quotes
  WHERE id = p_quote_id
  FOR UPDATE;

  IF v_quote_status <> 'submitted' THEN
    -- withdrawn, superseded, accepted, not_selected
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_NOT_AVAILABLE';
  END IF;

  IF v_valid_until <= v_now THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'QUOTE_EXPIRED';
  END IF;

  -- (3) Tạo đơn và cập nhật trạng thái liên quan.
  INSERT INTO orders (
    quote_id, design_request_id, customer_id, bakery_id,
    agreed_price, currency, pickup_at, status, created_at, updated_at
  ) VALUES (
    p_quote_id, v_request_id, p_customer_id, v_bakery_id,
    v_price, v_currency, v_pickup_at, 'pending_confirmation', v_now, v_now
  );

  SET p_order_id = LAST_INSERT_ID();

  UPDATE quotes
     SET status = 'accepted', updated_at = v_now
   WHERE id = p_quote_id;

  UPDATE quotes
     SET status = 'not_selected', updated_at = v_now
   WHERE design_request_id = v_request_id
     AND id <> p_quote_id
     AND status = 'submitted';

  UPDATE design_requests
     SET status = 'ordered', updated_at = v_now
   WHERE id = v_request_id;

  COMMIT;
END$$

-- -----------------------------------------------------------------------------
-- sp_bakery_decide_order
--   p_order_id   : đơn cần xử lý
--   p_user_id    : nhân viên tiệm đang đăng nhập
--   p_decision   : 'confirm' hoặc 'reject'
--   p_reason     : lý do từ chối (tùy chọn)
--
-- Chỉ thành viên đang hoạt động của đúng tiệm mới quyết định được, và chỉ khi đơn
-- đang 'pending_confirmation'. Khi tiệm từ chối, yêu cầu chuyển 'cancelled'
-- (giả định v1: khách tạo yêu cầu mới).
-- -----------------------------------------------------------------------------
CREATE PROCEDURE sp_bakery_decide_order(
  IN p_order_id BIGINT,
  IN p_user_id  BIGINT,
  IN p_decision VARCHAR(10),
  IN p_reason   VARCHAR(500)
)
BEGIN
  DECLARE v_now        DATETIME(3);
  DECLARE v_bakery_id  BIGINT;
  DECLARE v_request_id BIGINT;
  DECLARE v_status     VARCHAR(30);
  DECLARE v_is_member  INT DEFAULT 0;

  DECLARE EXIT HANDLER FOR SQLEXCEPTION
  BEGIN
    ROLLBACK;
    RESIGNAL;
  END;

  SET v_now = UTC_TIMESTAMP(3);

  IF p_decision IS NULL OR p_decision NOT IN ('confirm', 'reject') THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_DECISION';
  END IF;

  START TRANSACTION;

  SELECT bakery_id, design_request_id, status
    INTO v_bakery_id, v_request_id, v_status
  FROM orders
  WHERE id = p_order_id
  FOR UPDATE;

  IF v_bakery_id IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_NOT_FOUND';
  END IF;

  SELECT COUNT(*) INTO v_is_member
  FROM bakery_members
  WHERE bakery_id = v_bakery_id
    AND user_id = p_user_id
    AND status = 'active';

  IF v_is_member = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'NOT_BAKERY_MEMBER';
  END IF;

  IF v_status <> 'pending_confirmation' THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'ORDER_NOT_PENDING';
  END IF;

  IF p_decision = 'confirm' THEN
    UPDATE orders
       SET status = 'confirmed',
           bakery_decided_at = v_now,
           bakery_decided_by_user_id = p_user_id,
           updated_at = v_now
     WHERE id = p_order_id;
  ELSE
    UPDATE orders
       SET status = 'rejected_by_bakery',
           bakery_decided_at = v_now,
           bakery_decided_by_user_id = p_user_id,
           rejection_reason = p_reason,
           updated_at = v_now
     WHERE id = p_order_id;

    UPDATE design_requests
       SET status = 'cancelled', updated_at = v_now
     WHERE id = v_request_id;
  END IF;

  COMMIT;
END$$

DELIMITER ;
