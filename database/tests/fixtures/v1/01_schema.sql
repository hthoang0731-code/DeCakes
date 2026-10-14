-- =============================================================================
-- DeCakes — 01_schema.sql
-- Tạo database và toàn bộ bảng cho phiên bản prototype đầu tiên.
--
-- Yêu cầu: MySQL 8.0.16 trở lên (để CHECK constraint được thực thi), InnoDB, utf8mb4.
--
-- FIXTURE KIỂM THỬ: bản schema v1 (commit a9a238e) giữ nguyên cấu trúc, chỉ đổi
-- DROP/CREATE DATABASE decakes thành USE decakes_db. Chỉ dùng để kiểm thử migration
-- V002; KHÔNG dùng để cài đặt.
--
-- Quy ước:
--   * Mọi cột thời gian là DATETIME(3) và lưu theo UTC (mặc định UTC_TIMESTAMP(3)).
--     Không dùng TIMESTAMP vì kiểu này tự đổi theo time_zone của session.
--     `updated_at` do ứng dụng gán (MySQL chỉ cho ON UPDATE CURRENT_TIMESTAMP, không phải UTC).
--   * Tiền lưu bằng DECIMAL(12,2); tiền tệ v1 chỉ có 'VND'.
--   * Trạng thái lưu bằng VARCHAR + CHECK (xem docs/database-design.md, mục 4).
--   * Ảnh nằm ngoài database; bảng media_assets chỉ lưu URL, loại ảnh và chủ sở hữu.
--   * Không có cột nào chứa mật khẩu dạng văn bản hay API key.
--   * MySQL cấm ON DELETE/ON UPDATE CASCADE hoặc SET NULL trên cột có trong CHECK,
--     vì vậy các khóa ngoại không khai báo hành động tham chiếu và dùng mặc định của
--     InnoDB (NO ACTION = RESTRICT): không xóa được dòng cha khi còn dòng con.
--     Prototype ưu tiên đổi trạng thái thay vì xóa.
-- =============================================================================

SET NAMES utf8mb4;
SET time_zone = '+00:00';

USE decakes_db;

-- -----------------------------------------------------------------------------
-- 1. users — tài khoản khách hàng, nhân viên tiệm hoặc quản trị viên
-- -----------------------------------------------------------------------------
CREATE TABLE users (
  id             BIGINT        NOT NULL AUTO_INCREMENT,
  email          VARCHAR(255)  NOT NULL,
  -- Chỉ lưu hash (vd. ASP.NET Core PasswordHasher / bcrypt). NULL = chưa đặt mật khẩu,
  -- không đăng nhập được (dùng cho tài khoản demo).
  password_hash  VARCHAR(255)  NULL,
  full_name      VARCHAR(150)  NOT NULL,
  phone          VARCHAR(20)   NULL,
  system_role    VARCHAR(20)   NOT NULL DEFAULT 'customer',
  status         VARCHAR(20)   NOT NULL DEFAULT 'active',
  is_demo        TINYINT(1)    NOT NULL DEFAULT 0,
  created_at     DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at     DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_users PRIMARY KEY (id),
  CONSTRAINT uq_users_email UNIQUE (email),
  CONSTRAINT chk_users_email CHECK (email LIKE '%_@_%'),
  CONSTRAINT chk_users_system_role CHECK (system_role IN ('customer', 'bakery', 'admin')),
  CONSTRAINT chk_users_status CHECK (status IN ('active', 'disabled')),
  CONSTRAINT chk_users_is_demo CHECK (is_demo IN (0, 1))
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 2. bakeries — hồ sơ tiệm bánh
-- -----------------------------------------------------------------------------
CREATE TABLE bakeries (
  id              BIGINT        NOT NULL AUTO_INCREMENT,
  name            VARCHAR(150)  NOT NULL,
  slug            VARCHAR(160)  NOT NULL,
  description     TEXT          NULL,
  address_line    VARCHAR(255)  NULL,
  city            VARCHAR(100)  NULL,
  phone           VARCHAR(20)   NULL,
  profile_status  VARCHAR(20)   NOT NULL DEFAULT 'draft',
  is_demo         TINYINT(1)    NOT NULL DEFAULT 0,
  created_at      DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at      DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_bakeries PRIMARY KEY (id),
  CONSTRAINT uq_bakeries_slug UNIQUE (slug),
  CONSTRAINT chk_bakeries_profile_status
    CHECK (profile_status IN ('draft', 'pending_review', 'active', 'suspended')),
  CONSTRAINT chk_bakeries_is_demo CHECK (is_demo IN (0, 1)),
  INDEX ix_bakeries_profile_status (profile_status)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 3. bakery_members — tài khoản nào làm việc cho tiệm nào
--    Không xóa dòng khi nhân viên nghỉ: đổi status = 'removed' để giữ lịch sử
--    (báo giá và xác nhận đơn tham chiếu đến bảng này).
-- -----------------------------------------------------------------------------
CREATE TABLE bakery_members (
  bakery_id    BIGINT       NOT NULL,
  user_id      BIGINT       NOT NULL,
  member_role  VARCHAR(20)  NOT NULL DEFAULT 'staff',
  status       VARCHAR(20)  NOT NULL DEFAULT 'active',
  created_at   DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at   DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_bakery_members PRIMARY KEY (bakery_id, user_id),
  CONSTRAINT fk_bakery_members_bakery FOREIGN KEY (bakery_id)
    REFERENCES bakeries (id),
  CONSTRAINT fk_bakery_members_user FOREIGN KEY (user_id)
    REFERENCES users (id),
  CONSTRAINT chk_bakery_members_role CHECK (member_role IN ('owner', 'staff')),
  CONSTRAINT chk_bakery_members_status CHECK (status IN ('active', 'removed')),
  INDEX ix_bakery_members_user (user_id)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 4. cakes — mẫu bánh của tiệm (catalog)
-- -----------------------------------------------------------------------------
CREATE TABLE cakes (
  id               BIGINT         NOT NULL AUTO_INCREMENT,
  bakery_id        BIGINT         NOT NULL,
  name             VARCHAR(150)   NOT NULL,
  description      TEXT           NULL,
  flavor           VARCHAR(200)   NULL,
  size_label       VARCHAR(100)   NULL,
  reference_price  DECIMAL(12,2)  NULL,
  currency         CHAR(3)        NOT NULL DEFAULT 'VND',
  visibility       VARCHAR(20)    NOT NULL DEFAULT 'draft',
  is_demo          TINYINT(1)     NOT NULL DEFAULT 0,
  created_at       DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at       DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_cakes PRIMARY KEY (id),
  -- Khóa ghép cho FK từ media_assets: ảnh mẫu bánh phải thuộc đúng tiệm sở hữu mẫu.
  CONSTRAINT uq_cakes_id_bakery UNIQUE (id, bakery_id),
  CONSTRAINT fk_cakes_bakery FOREIGN KEY (bakery_id)
    REFERENCES bakeries (id),
  CONSTRAINT chk_cakes_reference_price
    CHECK (reference_price IS NULL OR reference_price >= 0),
  CONSTRAINT chk_cakes_currency CHECK (currency IN ('VND')),
  CONSTRAINT chk_cakes_visibility CHECK (visibility IN ('draft', 'published', 'hidden')),
  CONSTRAINT chk_cakes_is_demo CHECK (is_demo IN (0, 1)),
  INDEX ix_cakes_bakery_visibility (bakery_id, visibility)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 5. design_requests — yêu cầu đặt bánh của khách
--    draft -> open -> ordered | cancelled
-- -----------------------------------------------------------------------------
CREATE TABLE design_requests (
  id                   BIGINT         NOT NULL AUTO_INCREMENT,
  customer_id          BIGINT         NOT NULL,
  inspired_by_cake_id  BIGINT         NULL,
  title                VARCHAR(200)   NOT NULL,
  idea_description     TEXT           NOT NULL,
  budget_min           DECIMAL(12,2)  NULL,
  budget_max           DECIMAL(12,2)  NOT NULL,
  currency             CHAR(3)        NOT NULL DEFAULT 'VND',
  size_description     VARCHAR(100)   NULL,
  servings             SMALLINT UNSIGNED NULL,
  flavor               VARCHAR(200)   NULL,
  pickup_at            DATETIME(3)    NOT NULL,   -- UTC
  status               VARCHAR(20)    NOT NULL DEFAULT 'draft',
  is_demo              TINYINT(1)     NOT NULL DEFAULT 0,
  created_at           DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at           DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_design_requests PRIMARY KEY (id),
  -- Khóa ghép cho FK từ orders: khách của đơn phải là chủ của yêu cầu.
  CONSTRAINT uq_design_requests_id_customer UNIQUE (id, customer_id),
  CONSTRAINT fk_design_requests_customer FOREIGN KEY (customer_id)
    REFERENCES users (id),
  CONSTRAINT fk_design_requests_cake FOREIGN KEY (inspired_by_cake_id)
    REFERENCES cakes (id),
  CONSTRAINT chk_design_requests_budget CHECK (
    budget_max > 0
    AND (budget_min IS NULL OR (budget_min >= 0 AND budget_min <= budget_max))
  ),
  CONSTRAINT chk_design_requests_currency CHECK (currency IN ('VND')),
  CONSTRAINT chk_design_requests_servings CHECK (servings IS NULL OR servings > 0),
  CONSTRAINT chk_design_requests_pickup CHECK (pickup_at > created_at),
  CONSTRAINT chk_design_requests_status
    CHECK (status IN ('draft', 'open', 'ordered', 'cancelled')),
  CONSTRAINT chk_design_requests_is_demo CHECK (is_demo IN (0, 1)),
  INDEX ix_design_requests_customer_status (customer_id, status),
  INDEX ix_design_requests_status_pickup (status, pickup_at)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 6. request_bakeries — yêu cầu được gửi đến những tiệm nào
--    pending -> viewed -> quoted | declined
-- -----------------------------------------------------------------------------
CREATE TABLE request_bakeries (
  id                 BIGINT        NOT NULL AUTO_INCREMENT,
  design_request_id  BIGINT        NOT NULL,
  bakery_id          BIGINT        NOT NULL,
  status             VARCHAR(20)   NOT NULL DEFAULT 'pending',
  decline_reason     VARCHAR(500)  NULL,
  sent_at            DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  responded_at       DATETIME(3)   NULL,
  updated_at         DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_request_bakeries PRIMARY KEY (id),
  -- Mỗi tiệm chỉ nhận một lần cho mỗi yêu cầu; cũng là đích của FK ghép từ quotes.
  CONSTRAINT uq_request_bakeries_request_bakery UNIQUE (design_request_id, bakery_id),
  CONSTRAINT fk_request_bakeries_request FOREIGN KEY (design_request_id)
    REFERENCES design_requests (id),
  CONSTRAINT fk_request_bakeries_bakery FOREIGN KEY (bakery_id)
    REFERENCES bakeries (id),
  CONSTRAINT chk_request_bakeries_status
    CHECK (status IN ('pending', 'viewed', 'quoted', 'declined')),
  CONSTRAINT chk_request_bakeries_responded CHECK (
    status NOT IN ('quoted', 'declined') OR responded_at IS NOT NULL
  ),
  INDEX ix_request_bakeries_bakery_status (bakery_id, status)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 7. media_assets — ảnh lưu ngoài DB; ở đây chỉ có URL, loại và chủ sở hữu
--
--   asset_kind          | gắn với              | owner_bakery_id
--   --------------------+----------------------+----------------
--   cake_photo          | cake_id              | bắt buộc (tiệm của mẫu bánh)
--   customer_reference  | design_request_id    | NULL (thuộc khách)
--   ai_generated        | design_request_id    | NULL (thuộc khách)
--   design_proposal     | design_request_id    | bắt buộc (tiệm đã nhận yêu cầu)
--
--   uploaded_by_user_id luôn là người tải lên / người yêu cầu tạo ảnh.
-- -----------------------------------------------------------------------------
CREATE TABLE media_assets (
  id                   BIGINT         NOT NULL AUTO_INCREMENT,
  asset_kind           VARCHAR(30)    NOT NULL,
  storage_url          VARCHAR(1024)  NOT NULL,
  mime_type            VARCHAR(50)    NOT NULL,
  width_px             INT UNSIGNED   NULL,
  height_px            INT UNSIGNED   NULL,
  uploaded_by_user_id  BIGINT         NOT NULL,
  owner_bakery_id      BIGINT         NULL,
  cake_id              BIGINT         NULL,
  design_request_id    BIGINT         NULL,
  version_no           INT UNSIGNED   NULL,   -- phiên bản thiết kế (design_proposal / ai_generated)
  caption              VARCHAR(255)   NULL,
  is_demo              TINYINT(1)     NOT NULL DEFAULT 0,
  created_at           DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_media_assets PRIMARY KEY (id),
  -- Khóa ghép cho FK từ quotes: thiết kế được xác nhận phải thuộc đúng yêu cầu.
  CONSTRAINT uq_media_assets_id_request UNIQUE (id, design_request_id),
  CONSTRAINT fk_media_assets_uploader FOREIGN KEY (uploaded_by_user_id)
    REFERENCES users (id),
  CONSTRAINT fk_media_assets_bakery FOREIGN KEY (owner_bakery_id)
    REFERENCES bakeries (id),
  CONSTRAINT fk_media_assets_request FOREIGN KEY (design_request_id)
    REFERENCES design_requests (id),
  -- Ảnh mẫu bánh: (cake_id, owner_bakery_id) phải khớp cakes(id, bakery_id).
  CONSTRAINT fk_media_assets_cake_bakery FOREIGN KEY (cake_id, owner_bakery_id)
    REFERENCES cakes (id, bakery_id),
  -- Ảnh đề xuất của tiệm: tiệm phải là tiệm đã nhận yêu cầu.
  -- (MySQL bỏ qua FK ghép khi có cột NULL, nên không ảnh hưởng ảnh của khách.)
  CONSTRAINT fk_media_assets_request_bakery FOREIGN KEY (design_request_id, owner_bakery_id)
    REFERENCES request_bakeries (design_request_id, bakery_id),
  CONSTRAINT chk_media_assets_kind CHECK (
    asset_kind IN ('cake_photo', 'customer_reference', 'ai_generated', 'design_proposal')
  ),
  CONSTRAINT chk_media_assets_owner_by_kind CHECK (
       (asset_kind = 'cake_photo'
          AND cake_id IS NOT NULL AND owner_bakery_id IS NOT NULL AND design_request_id IS NULL)
    OR (asset_kind IN ('customer_reference', 'ai_generated')
          AND design_request_id IS NOT NULL AND owner_bakery_id IS NULL AND cake_id IS NULL)
    OR (asset_kind = 'design_proposal'
          AND design_request_id IS NOT NULL AND owner_bakery_id IS NOT NULL AND cake_id IS NULL)
  ),
  CONSTRAINT chk_media_assets_url CHECK (storage_url LIKE 'https://%'),
  CONSTRAINT chk_media_assets_mime
    CHECK (mime_type IN ('image/jpeg', 'image/png', 'image/webp')),
  CONSTRAINT chk_media_assets_version CHECK (version_no IS NULL OR version_no >= 1),
  CONSTRAINT chk_media_assets_is_demo CHECK (is_demo IN (0, 1)),
  INDEX ix_media_assets_cake (cake_id),
  INDEX ix_media_assets_request_kind (design_request_id, asset_kind)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 8. quotes — báo giá của một tiệm cho một yêu cầu
--    submitted -> accepted | not_selected | withdrawn | superseded
--    "Hết hạn" KHÔNG phải trạng thái lưu: báo giá hết hạn khi valid_until <= UTC hiện tại,
--    được kiểm tra đúng lúc khách chọn (sp_accept_quote).
--    Sửa báo giá = tạo dòng mới với revision_no + 1, dòng cũ chuyển 'superseded'.
-- -----------------------------------------------------------------------------
CREATE TABLE quotes (
  id                         BIGINT         NOT NULL AUTO_INCREMENT,
  design_request_id          BIGINT         NOT NULL,
  bakery_id                  BIGINT         NOT NULL,
  revision_no                INT UNSIGNED   NOT NULL DEFAULT 1,
  price                      DECIMAL(12,2)  NOT NULL,
  currency                   CHAR(3)        NOT NULL DEFAULT 'VND',
  note                       TEXT           NULL,
  estimated_pickup_at        DATETIME(3)    NOT NULL,   -- UTC, thời gian nhận dự kiến
  valid_until                DATETIME(3)    NOT NULL,   -- UTC, hạn báo giá
  confirmed_design_asset_id  BIGINT         NULL,       -- phiên bản thiết kế được xác nhận
  created_by_user_id         BIGINT         NOT NULL,
  status                     VARCHAR(20)    NOT NULL DEFAULT 'submitted',
  withdrawn_at               DATETIME(3)    NULL,
  created_at                 DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at                 DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_quotes PRIMARY KEY (id),
  CONSTRAINT uq_quotes_revision UNIQUE (design_request_id, bakery_id, revision_no),
  -- Khóa ghép cho FK từ orders: đơn phải khớp yêu cầu + tiệm của báo giá.
  CONSTRAINT uq_quotes_id_request_bakery UNIQUE (id, design_request_id, bakery_id),
  -- Chỉ tiệm đã nhận yêu cầu mới báo giá được.
  CONSTRAINT fk_quotes_request_bakery FOREIGN KEY (design_request_id, bakery_id)
    REFERENCES request_bakeries (design_request_id, bakery_id),
  -- Người tạo báo giá phải là thành viên của tiệm.
  CONSTRAINT fk_quotes_created_by_member FOREIGN KEY (bakery_id, created_by_user_id)
    REFERENCES bakery_members (bakery_id, user_id),
  -- Thiết kế được xác nhận phải là ảnh gắn với cùng yêu cầu.
  CONSTRAINT fk_quotes_confirmed_design FOREIGN KEY (confirmed_design_asset_id, design_request_id)
    REFERENCES media_assets (id, design_request_id),
  CONSTRAINT chk_quotes_price CHECK (price > 0),
  CONSTRAINT chk_quotes_currency CHECK (currency IN ('VND')),
  CONSTRAINT chk_quotes_revision CHECK (revision_no >= 1),
  CONSTRAINT chk_quotes_valid_until CHECK (valid_until > created_at),
  CONSTRAINT chk_quotes_estimated_pickup CHECK (estimated_pickup_at > created_at),
  CONSTRAINT chk_quotes_status
    CHECK (status IN ('submitted', 'withdrawn', 'superseded', 'accepted', 'not_selected')),
  CONSTRAINT chk_quotes_withdrawn CHECK ((status = 'withdrawn') = (withdrawn_at IS NOT NULL)),
  INDEX ix_quotes_request_status (design_request_id, status),
  INDEX ix_quotes_bakery_status (bakery_id, status)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 9. orders — đơn tạo khi khách chọn báo giá
--    pending_confirmation -> confirmed -> ready -> picked_up
--    pending_confirmation -> rejected_by_bakery
--    pending_confirmation | confirmed -> cancelled
--
--  * UNIQUE(quote_id): một báo giá tạo tối đa một đơn.
--  * UNIQUE(design_request_id): một yêu cầu tạo tối đa một đơn (giả định v1).
--  * Đơn chỉ rời pending_confirmation (trừ khi hủy) khi đã có thời điểm và người
--    của tiệm quyết định; người đó phải là thành viên của đúng tiệm (FK ghép).
-- -----------------------------------------------------------------------------
CREATE TABLE orders (
  id                         BIGINT         NOT NULL AUTO_INCREMENT,
  quote_id                   BIGINT         NOT NULL,
  design_request_id          BIGINT         NOT NULL,
  customer_id                BIGINT         NOT NULL,
  bakery_id                  BIGINT         NOT NULL,
  agreed_price               DECIMAL(12,2)  NOT NULL,   -- bản sao giá tại thời điểm chọn
  currency                   CHAR(3)        NOT NULL DEFAULT 'VND',
  pickup_at                  DATETIME(3)    NOT NULL,   -- UTC
  status                     VARCHAR(30)    NOT NULL DEFAULT 'pending_confirmation',
  bakery_decided_at          DATETIME(3)    NULL,       -- tiệm xác nhận / từ chối lúc nào
  bakery_decided_by_user_id  BIGINT         NULL,       -- ai trong tiệm quyết định
  rejection_reason           VARCHAR(500)   NULL,
  picked_up_at               DATETIME(3)    NULL,
  cancelled_at               DATETIME(3)    NULL,
  created_at                 DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at                 DATETIME(3)    NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_orders PRIMARY KEY (id),
  CONSTRAINT uq_orders_quote UNIQUE (quote_id),
  CONSTRAINT uq_orders_design_request UNIQUE (design_request_id),
  CONSTRAINT fk_orders_quote FOREIGN KEY (quote_id, design_request_id, bakery_id)
    REFERENCES quotes (id, design_request_id, bakery_id),
  CONSTRAINT fk_orders_request_customer FOREIGN KEY (design_request_id, customer_id)
    REFERENCES design_requests (id, customer_id),
  CONSTRAINT fk_orders_bakery FOREIGN KEY (bakery_id)
    REFERENCES bakeries (id),
  CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id)
    REFERENCES users (id),
  CONSTRAINT fk_orders_decided_by_member FOREIGN KEY (bakery_id, bakery_decided_by_user_id)
    REFERENCES bakery_members (bakery_id, user_id),
  CONSTRAINT chk_orders_status CHECK (status IN (
    'pending_confirmation', 'confirmed', 'rejected_by_bakery', 'ready', 'picked_up', 'cancelled'
  )),
  CONSTRAINT chk_orders_price CHECK (agreed_price > 0),
  CONSTRAINT chk_orders_currency CHECK (currency IN ('VND')),
  -- Tiệm phải xác nhận trước khi đơn sang confirmed / ready / picked_up.
  CONSTRAINT chk_orders_decision CHECK (
       (status = 'pending_confirmation'
          AND bakery_decided_at IS NULL AND bakery_decided_by_user_id IS NULL)
    OR (status IN ('confirmed', 'rejected_by_bakery', 'ready', 'picked_up')
          AND bakery_decided_at IS NOT NULL AND bakery_decided_by_user_id IS NOT NULL)
    OR (status = 'cancelled')
  ),
  CONSTRAINT chk_orders_picked_up CHECK ((status = 'picked_up') = (picked_up_at IS NOT NULL)),
  CONSTRAINT chk_orders_cancelled CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL)),
  INDEX ix_orders_bakery_status (bakery_id, status),
  INDEX ix_orders_customer_status (customer_id, status)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;
