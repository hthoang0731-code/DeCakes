-- =============================================================================
-- DeCakes — migrations/V004__bakery_onboarding.sql   (ĐỀ XUẤT, chưa áp dụng)
--
-- Chủ tiệm tự đăng ký, hồ sơ chờ admin duyệt, gói theo tiệm, trial khi duyệt, lời mời staff.
-- Chạy sau V003. KHÔNG tự chạy lên Aiven; chỉ đã nạp trên MySQL 8.4 kiểm thử dùng một lần.
-- Sao lưu trước khi chạy. SAU KHI CHẠY: V004__bakery_onboarding.grants.sql cho mọi tài khoản backend.
--
-- Thay đổi:
--   1. bakeries.profile_status thêm 'rejected' (admin từ chối, kết thúc). Không có dòng nào bị đổi.
--   2. bakery_review_events: lịch sử gửi duyệt/quyết định (ai, lúc nào, lý do). Chỉ thêm dòng.
--   3. subscription_plans: danh mục gói do người vận hành quản lý (backend chỉ SELECT). Giá NULL =
--      chưa định giá: được chọn nhưng không mở checkout. Không có dòng mặc định, không có giá 0.
--   4. bakery_subscriptions: gói tiệm đã chọn (một dòng/tiệm).
--   5. bakery_subscription_periods: các kỳ được dùng dịch vụ (trial/paid). Quyền tại thời điểm t có
--      khi tồn tại kỳ starts_at <= t < ends_at. UNIQUE trên cột sinh trial_bakery_id: mỗi tiệm tối đa
--      một kỳ trial, kể cả khi duyệt đồng thời/duyệt lại. Chỉ thêm dòng.
--   6. bakery_invitations: lời mời staff theo tiệm + email; chỉ lưu SHA-256 của token, có hạn,
--      dùng một lần, thu hồi được.
--
-- KHÔNG cấp trial/kỳ cho tiệm active hiện có (không dùng updated_at làm ngày duyệt). Khi V005 bật
-- kiểm tra quyền gói trong procedure, các tiệm này bị chặn giao dịch mới cho tới khi có kế hoạch
-- chuyển tiếp riêng (xem docs/ACCOUNTS_AUTHORIZATION_PLAN.md).
-- =============================================================================

SET NAMES utf8mb4;

USE decakes_db;

DROP PROCEDURE IF EXISTS tmp_v004_precheck;

DELIMITER $$

CREATE PROCEDURE tmp_v004_precheck()
BEGIN
  DECLARE v_v3 INT DEFAULT 0;
  DECLARE v_v4 INT DEFAULT 0;
  SELECT COUNT(*) INTO v_v3 FROM schema_migrations WHERE version = 3;
  SELECT COUNT(*) INTO v_v4 FROM schema_migrations WHERE version = 4;
  IF v_v3 <> 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V004_REQUIRES_V3';
  END IF;
  IF v_v4 <> 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V004_ALREADY_APPLIED';
  END IF;
END$$

DELIMITER ;

CALL tmp_v004_precheck();

DROP PROCEDURE tmp_v004_precheck;

ALTER TABLE bakeries DROP CHECK chk_bakeries_profile_status;

ALTER TABLE bakeries ADD CONSTRAINT chk_bakeries_profile_status
  CHECK (profile_status IN ('draft', 'pending_review', 'active', 'suspended', 'rejected'));

CREATE TABLE bakery_review_events (
  id             BIGINT         NOT NULL AUTO_INCREMENT,
  bakery_id      BIGINT         NOT NULL,
  event_type     VARCHAR(30)    NOT NULL,
  actor_user_id  BIGINT         NOT NULL,
  reason         VARCHAR(1000)  NULL,
  created_at     DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_bakery_review_events PRIMARY KEY (id),
  CONSTRAINT fk_bakery_review_events_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_bakery_review_events_actor FOREIGN KEY (actor_user_id) REFERENCES users (id),
  CONSTRAINT chk_bakery_review_events_type
    CHECK (event_type IN ('submitted', 'approved', 'changes_requested', 'rejected')),
  -- Yêu cầu sửa và từ chối bắt buộc có lý do.
  CONSTRAINT chk_bakery_review_events_reason
    CHECK (event_type NOT IN ('changes_requested', 'rejected') OR (reason IS NOT NULL AND TRIM(reason) <> '')),
  INDEX ix_bakery_review_events_bakery (bakery_id, created_at)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE subscription_plans (
  id             BIGINT         NOT NULL AUTO_INCREMENT,
  code           VARCHAR(50)    NOT NULL,
  name           VARCHAR(100)   NOT NULL,
  -- draft: chưa cho chọn; active: được chọn; retired: không cho chọn mới.
  status         VARCHAR(20)    NOT NULL DEFAULT 'draft',
  -- NULL = chưa định giá. VND không có phần lẻ (payOS nhận số nguyên).
  monthly_price  DECIMAL(12,2)  NULL,
  yearly_price   DECIMAL(12,2)  NULL,
  currency       CHAR(3)        NOT NULL DEFAULT 'VND',
  created_at     DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at     DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_subscription_plans PRIMARY KEY (id),
  CONSTRAINT uq_subscription_plans_code UNIQUE (code),
  CONSTRAINT chk_subscription_plans_status CHECK (status IN ('draft', 'active', 'retired')),
  CONSTRAINT chk_subscription_plans_monthly
    CHECK (monthly_price IS NULL OR (monthly_price > 0 AND monthly_price = FLOOR(monthly_price))),
  CONSTRAINT chk_subscription_plans_yearly
    CHECK (yearly_price IS NULL OR (yearly_price > 0 AND yearly_price = FLOOR(yearly_price))),
  CONSTRAINT chk_subscription_plans_currency CHECK (currency IN ('VND'))
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE bakery_subscriptions (
  bakery_id            BIGINT       NOT NULL,
  plan_id              BIGINT       NOT NULL,
  selected_by_user_id  BIGINT       NOT NULL,
  created_at           DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at           DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_bakery_subscriptions PRIMARY KEY (bakery_id),
  CONSTRAINT fk_bakery_subscriptions_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_bakery_subscriptions_plan FOREIGN KEY (plan_id) REFERENCES subscription_plans (id),
  CONSTRAINT fk_bakery_subscriptions_user FOREIGN KEY (selected_by_user_id) REFERENCES users (id)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE bakery_subscription_periods (
  id               BIGINT       NOT NULL AUTO_INCREMENT,
  bakery_id        BIGINT       NOT NULL,
  plan_id          BIGINT       NOT NULL,
  period_kind      VARCHAR(20)  NOT NULL,
  starts_at        DATETIME(3)  NOT NULL,
  ends_at          DATETIME(3)  NOT NULL,
  created_at       DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  trial_bakery_id  BIGINT       GENERATED ALWAYS AS (CASE WHEN period_kind = 'trial' THEN bakery_id END) STORED,

  CONSTRAINT pk_bakery_subscription_periods PRIMARY KEY (id),
  CONSTRAINT uq_bakery_subscription_periods_one_trial UNIQUE (trial_bakery_id),
  CONSTRAINT fk_bakery_subscription_periods_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_bakery_subscription_periods_plan FOREIGN KEY (plan_id) REFERENCES subscription_plans (id),
  CONSTRAINT chk_bakery_subscription_periods_kind CHECK (period_kind IN ('trial', 'paid')),
  CONSTRAINT chk_bakery_subscription_periods_range CHECK (ends_at > starts_at),
  INDEX ix_bakery_subscription_periods_bakery_end (bakery_id, ends_at)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

CREATE TABLE bakery_invitations (
  id                  BIGINT        NOT NULL AUTO_INCREMENT,
  bakery_id           BIGINT        NOT NULL,
  email               VARCHAR(255)  NOT NULL,
  token_hash          BINARY(32)    NOT NULL,
  invited_by_user_id  BIGINT        NOT NULL,
  created_at          DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  expires_at          DATETIME(3)   NOT NULL,
  accepted_at         DATETIME(3)   NULL,
  accepted_user_id    BIGINT        NULL,
  revoked_at          DATETIME(3)   NULL,

  CONSTRAINT pk_bakery_invitations PRIMARY KEY (id),
  CONSTRAINT uq_bakery_invitations_hash UNIQUE (token_hash),
  CONSTRAINT fk_bakery_invitations_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_bakery_invitations_inviter FOREIGN KEY (invited_by_user_id) REFERENCES users (id),
  CONSTRAINT fk_bakery_invitations_accepted_user FOREIGN KEY (accepted_user_id) REFERENCES users (id),
  CONSTRAINT chk_bakery_invitations_expiry CHECK (expires_at > created_at),
  CONSTRAINT chk_bakery_invitations_accepted CHECK ((accepted_at IS NULL) = (accepted_user_id IS NULL)),
  CONSTRAINT chk_bakery_invitations_outcome CHECK (accepted_at IS NULL OR revoked_at IS NULL),
  INDEX ix_bakery_invitations_bakery_email (bakery_id, email)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

INSERT INTO schema_migrations (version, description)
VALUES (4, 'bakery onboarding: review history, rejected status, plans, trial periods, staff invitations');
