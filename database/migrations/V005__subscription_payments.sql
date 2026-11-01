-- =============================================================================
-- DeCakes — migrations/V005__subscription_payments.sql   (ĐỀ XUẤT, chưa áp dụng)
--
-- Thu phí gói theo tiệm và chặn giao dịch mới khi tiệm không có kỳ sử dụng còn hạn.
-- Chạy sau V004. KHÔNG tự chạy lên Aiven; chỉ đã nạp trên MySQL 8.4 kiểm thử dùng một lần.
-- Sao lưu trước khi chạy. SAU KHI CHẠY: V005__subscription_payments.grants.sql cho mọi tài
-- khoản backend.
--
-- Thay đổi:
--   1. subscription_payments: giao dịch phí sử dụng DeCakes (KHÔNG phải tiền bánh). Số tiền, gói,
--      chu kỳ chốt lúc tạo checkout (sửa giá danh mục sau đó không đổi giao dịch đã tạo).
--      order_code UNIQUE là mã gửi cổng thanh toán; trạng thái paid chỉ đặt sau khi xác minh
--      webhook có chữ ký và đối chiếu đúng số tiền/tiền tệ/mã giao dịch.
--   2. bakery_subscription_periods.payment_id UNIQUE: mỗi giao dịch tạo tối đa một kỳ trả phí,
--      nên webhook lặp/đến trễ không gia hạn hai lần. Kỳ trả phí bắt buộc có payment_id.
--   3. sp_guard_bakery_entitled + tạo lại 4 procedure (nội dung giữ nguyên install/02, chỉ thêm
--      một lệnh CALL guard ngay sau khi lấy v_now, tức là sau khi đã giữ đủ khóa):
--        sp_send_request_to_bakery (nhận yêu cầu mới), sp_create_quote, sp_revise_quote
--        (tạo/sửa báo giá), sp_accept_quote (tạo đơn mới, kể cả từ báo giá tạo trước hết hạn).
--      KHÔNG đổi sp_withdraw_quote, sp_bakery_decide_order: rút báo giá, xác nhận/từ chối đơn
--      đã có vẫn chạy khi hết hạn. Hết hạn không đổi users.status, bakeries.profile_status,
--      không hủy đơn. Các procedure vẫn tự quản lý transaction.
--
-- ẢNH HƯỞNG TRIỂN KHAI: tiệm active hiện có chưa có kỳ sử dụng nào (V004 không cấp trial hàng
-- loạt) sẽ bị chặn nhận yêu cầu/báo giá/đơn mới ngay khi chạy file này. Cần kế hoạch chuyển
-- tiếp riêng được duyệt trước khi áp dụng (xem docs/ACCOUNTS_AUTHORIZATION_PLAN.md).
-- =============================================================================

SET NAMES utf8mb4;

USE decakes_db;

DROP PROCEDURE IF EXISTS tmp_v005_precheck;

DELIMITER $$

CREATE PROCEDURE tmp_v005_precheck()
BEGIN
  DECLARE v_v4 INT DEFAULT 0;
  DECLARE v_v5 INT DEFAULT 0;
  SELECT COUNT(*) INTO v_v4 FROM schema_migrations WHERE version = 4;
  SELECT COUNT(*) INTO v_v5 FROM schema_migrations WHERE version = 5;
  IF v_v4 <> 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V005_REQUIRES_V4';
  END IF;
  IF v_v5 <> 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V005_ALREADY_APPLIED';
  END IF;
END$$

DELIMITER ;

CALL tmp_v005_precheck();

DROP PROCEDURE tmp_v005_precheck;

CREATE TABLE subscription_payments (
  id                   BIGINT         NOT NULL AUTO_INCREMENT,
  bakery_id            BIGINT         NOT NULL,
  plan_id              BIGINT         NOT NULL,
  billing_cycle        VARCHAR(10)    NOT NULL,
  amount               DECIMAL(12,2)  NOT NULL,
  currency             CHAR(3)        NOT NULL DEFAULT 'VND',
  provider             VARCHAR(20)    NOT NULL,
  order_code           BIGINT         NOT NULL,
  provider_payment_id  VARCHAR(100)   NULL,
  checkout_url         VARCHAR(1024)  NULL,
  -- created: đã ghi, chưa có link; pending: đã có link; paid: đã xác minh; failed: tạo link lỗi;
  -- mismatch: webhook hợp lệ nhưng không khớp số tiền/tiền tệ/mã giao dịch (cần đối soát).
  status               VARCHAR(20)    NOT NULL DEFAULT 'created',
  provider_reference   VARCHAR(100)   NULL,
  failure_reason       VARCHAR(200)   NULL,
  created_by_user_id   BIGINT         NOT NULL,
  created_at           DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at           DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  paid_at              DATETIME(3)    NULL,

  CONSTRAINT pk_subscription_payments PRIMARY KEY (id),
  CONSTRAINT uq_subscription_payments_order_code UNIQUE (provider, order_code),
  CONSTRAINT fk_subscription_payments_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_subscription_payments_plan FOREIGN KEY (plan_id) REFERENCES subscription_plans (id),
  CONSTRAINT fk_subscription_payments_user FOREIGN KEY (created_by_user_id) REFERENCES users (id),
  CONSTRAINT chk_subscription_payments_cycle CHECK (billing_cycle IN ('month', 'year')),
  CONSTRAINT chk_subscription_payments_amount CHECK (amount > 0 AND amount = FLOOR(amount)),
  CONSTRAINT chk_subscription_payments_currency CHECK (currency IN ('VND')),
  CONSTRAINT chk_subscription_payments_provider CHECK (provider IN ('payos')),
  CONSTRAINT chk_subscription_payments_order_code CHECK (order_code > 0),
  CONSTRAINT chk_subscription_payments_status CHECK (status IN ('created', 'pending', 'paid', 'failed', 'mismatch')),
  CONSTRAINT chk_subscription_payments_paid CHECK (status <> 'paid' OR paid_at IS NOT NULL),
  INDEX ix_subscription_payments_bakery (bakery_id, created_at)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

ALTER TABLE bakery_subscription_periods
  ADD COLUMN payment_id BIGINT NULL AFTER ends_at,
  ADD CONSTRAINT uq_bakery_subscription_periods_payment UNIQUE (payment_id),
  ADD CONSTRAINT fk_bakery_subscription_periods_payment FOREIGN KEY (payment_id) REFERENCES subscription_payments (id),
  ADD CONSTRAINT chk_bakery_subscription_periods_payment CHECK ((period_kind = 'paid') = (payment_id IS NOT NULL));

DELIMITER $$

DROP PROCEDURE IF EXISTS sp_guard_bakery_entitled$$

-- ----- BLOCK: sp_guard_bakery_entitled -----
-- Tiệm phải có một kỳ sử dụng (trial hoặc trả phí) chứa p_now. Không có ngoại lệ cho tiệm chưa
-- có dòng gói nào. Đọc khóa chia sẻ như các bảng tham chiếu khác (sau khóa nghiệp vụ).
CREATE PROCEDURE sp_guard_bakery_entitled(IN p_bakery_id BIGINT, IN p_now DATETIME(3))
BEGIN
  DECLARE v_count INT DEFAULT 0;
  IF p_bakery_id IS NULL OR p_now IS NULL THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'INVALID_ARGUMENT';
  END IF;
  SELECT COUNT(*) INTO v_count
  FROM bakery_subscription_periods
  WHERE bakery_id = p_bakery_id AND starts_at <= p_now AND ends_at > p_now
  FOR SHARE;
  IF v_count = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'BAKERY_SUBSCRIPTION_INACTIVE';
  END IF;
END$$

DROP PROCEDURE IF EXISTS sp_send_request_to_bakery$$

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
  -- V005: quyền gói của tiệm tại v_now (sau khi đã giữ khóa).
  CALL sp_guard_bakery_entitled(p_bakery_id, v_now);
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

DROP PROCEDURE IF EXISTS sp_create_quote$$

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
  -- V005: quyền gói của tiệm tại v_now (sau khi đã giữ khóa).
  CALL sp_guard_bakery_entitled(p_bakery_id, v_now);
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

DROP PROCEDURE IF EXISTS sp_revise_quote$$

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
  -- V005: quyền gói của tiệm tại v_now (sau khi đã giữ khóa).
  CALL sp_guard_bakery_entitled(v_bakery_id, v_now);
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

DROP PROCEDURE IF EXISTS sp_accept_quote$$

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
  -- V005: quyền gói của tiệm tại v_now (sau khi đã giữ khóa).
  CALL sp_guard_bakery_entitled(v_bakery_id, v_now);
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

DELIMITER ;

INSERT INTO schema_migrations (version, description)
VALUES (5, 'subscription payments, paid periods, entitlement guard in request/quote/accept procedures');
