-- =============================================================================
-- DeCakes — install/01_schema.sql   (schema phiên bản 2)
--
-- Tạo toàn bộ bảng trong database `decakes_db` ĐÃ CÓ SẴN và CHƯA CÓ BẢNG.
--   * Aiven: database `decakes_db` đã được tạo trên Aiven Console.
--   * MySQL trên máy: chạy install/00_create_database_local.sql trước.
--
-- Script KHÔNG xóa database và KHÔNG dùng CREATE TABLE IF NOT EXISTS: nếu đã có bảng
-- (vd. schema v1 đã triển khai), lệnh CREATE TABLE đầu tiên sẽ báo lỗi
-- "Table 'schema_migrations' already exists" nếu bảng phiên bản đã có. Nếu database
-- v1 chưa có bảng phiên bản, không chạy script cài mới; dùng migrations/V002__*.sql.
--
-- Yêu cầu: MySQL 8.0.16+ (đã nhắm tới Aiven MySQL 8.4), InnoDB, utf8mb4.
--
-- Quy ước:
--   * Thời gian: DATETIME(3), luôn là UTC. Không dùng TIMESTAMP (tự đổi theo time_zone
--     của session). `updated_at` do ứng dụng / stored procedure gán.
--   * Tiền: DECIMAL(12,2); v1 chỉ có 'VND'.
--   * Trạng thái: VARCHAR + CHECK (lý do: docs/database-design.md, mục 5).
--   * Ảnh nằm ngoài DB; media_assets chỉ lưu URL, loại ảnh và chủ sở hữu.
--   * Không có cột chứa mật khẩu dạng văn bản hay API key.
--   * Khóa ngoại không khai báo ON DELETE/ON UPDATE (mặc định NO ACTION = chặn xóa
--     dòng cha khi còn dòng con). MySQL cấm CASCADE/SET NULL trên cột nằm trong CHECK,
--     và prototype ưu tiên đổi trạng thái thay vì xóa.
--   * Mọi khóa ngoại trỏ tới PRIMARY KEY hoặc UNIQUE đầy đủ cột (MySQL 8.4 mặc định
--     từ chối FK trỏ tới khóa không UNIQUE — restrict_fk_on_non_standard_key).
--   * Mọi bảng có PRIMARY KEY (Aiven bật sql_require_primary_key).
-- =============================================================================

SET NAMES utf8mb4;

USE decakes_db;

-- -----------------------------------------------------------------------------
-- 0. schema_migrations — ghi lại phiên bản schema đã áp dụng
-- -----------------------------------------------------------------------------
CREATE TABLE schema_migrations (
  version      INT           NOT NULL,
  description  VARCHAR(200)  NOT NULL,
  applied_at   DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_schema_migrations PRIMARY KEY (version)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

-- -----------------------------------------------------------------------------
-- 1. users — tài khoản đăng nhập website (khách, nhân viên tiệm, quản trị viên)
--    Đây KHÔNG phải tài khoản MySQL. Backend xác thực người dùng (ASP.NET Core
--    Identity / cookie / JWT) rồi mới truyền users.id vào stored procedure.
-- -----------------------------------------------------------------------------
CREATE TABLE users (
  id             BIGINT        NOT NULL AUTO_INCREMENT,
  email          VARCHAR(255)  NOT NULL,
  -- Chỉ lưu hash (vd. ASP.NET Core PasswordHasher). NULL = chưa đặt mật khẩu,
  -- không đăng nhập được (tài khoản demo).
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
--    Chỉ tiệm 'active' được nhận yêu cầu mới, gửi/sửa báo giá, được khách chọn và
--    xác nhận đơn. Tiệm không active vẫn được TỪ CHỐI đơn đang chờ (để giải phóng khách).
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
--    Không xóa dòng khi nhân viên nghỉ: đổi status = 'removed' (báo giá và xác nhận
--    đơn tham chiếu tới bảng này). Thành viên 'removed' không thao tác được.
-- -----------------------------------------------------------------------------
CREATE TABLE bakery_members (
  bakery_id    BIGINT       NOT NULL,
  user_id      BIGINT       NOT NULL,
  member_role  VARCHAR(20)  NOT NULL DEFAULT 'staff',
  status       VARCHAR(20)  NOT NULL DEFAULT 'active',
  created_at   DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at   DATETIME(3)  NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_bakery_members PRIMARY KEY (bakery_id, user_id),
  CONSTRAINT fk_bakery_members_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_bakery_members_user FOREIGN KEY (user_id) REFERENCES users (id),
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
  -- Đích của FK ghép từ media_assets: ảnh mẫu bánh phải thuộc đúng tiệm sở hữu mẫu.
  CONSTRAINT uq_cakes_id_bakery UNIQUE (id, bakery_id),
  CONSTRAINT fk_cakes_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
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
--    Khách có thể sửa yêu cầu khi còn draft/open; đơn đã tạo KHÔNG phụ thuộc các cột
--    này nữa (xem orders.agreed_*).
-- -----------------------------------------------------------------------------
CREATE TABLE design_requests (
  id                   BIGINT            NOT NULL AUTO_INCREMENT,
  customer_id          BIGINT            NOT NULL,
  inspired_by_cake_id  BIGINT            NULL,
  title                VARCHAR(200)      NOT NULL,
  idea_description     TEXT              NOT NULL,
  budget_min           DECIMAL(12,2)     NULL,
  budget_max           DECIMAL(12,2)     NOT NULL,
  currency             CHAR(3)           NOT NULL DEFAULT 'VND',
  size_description     VARCHAR(100)      NULL,
  servings             SMALLINT UNSIGNED NULL,
  flavor               VARCHAR(200)      NULL,
  inscription_text     VARCHAR(200)      NULL,   -- chữ khách muốn ghi trên bánh (v2)
  pickup_at            DATETIME(3)       NOT NULL,   -- UTC
  status               VARCHAR(20)       NOT NULL DEFAULT 'draft',
  is_demo              TINYINT(1)        NOT NULL DEFAULT 0,
  created_at           DATETIME(3)       NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at           DATETIME(3)       NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_design_requests PRIMARY KEY (id),
  -- Đích của FK ghép từ orders: khách của đơn phải là chủ của yêu cầu.
  CONSTRAINT uq_design_requests_id_customer UNIQUE (id, customer_id),
  CONSTRAINT fk_design_requests_customer FOREIGN KEY (customer_id) REFERENCES users (id),
  CONSTRAINT fk_design_requests_cake FOREIGN KEY (inspired_by_cake_id) REFERENCES cakes (id),
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
--    Thêm dòng qua sp_send_request_to_bakery (chỉ gửi được cho tiệm 'active').
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
  -- Mỗi tiệm nhận một lần cho mỗi yêu cầu; cũng là đích của FK ghép từ quotes.
  CONSTRAINT uq_request_bakeries_request_bakery UNIQUE (design_request_id, bakery_id),
  CONSTRAINT fk_request_bakeries_request FOREIGN KEY (design_request_id) REFERENCES design_requests (id),
  CONSTRAINT fk_request_bakeries_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
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
--   Ảnh là BẤT BIẾN: thay ảnh = thêm dòng mới (version_no tăng), không UPDATE dòng cũ.
--   Backend phải lưu file với khóa không bị ghi đè (vd. GUID), vì đơn hàng sao chép URL.
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
  -- Đích của FK ghép từ quotes: thiết kế của báo giá phải thuộc cùng yêu cầu.
  CONSTRAINT uq_media_assets_id_request UNIQUE (id, design_request_id),
  CONSTRAINT fk_media_assets_uploader FOREIGN KEY (uploaded_by_user_id) REFERENCES users (id),
  CONSTRAINT fk_media_assets_bakery FOREIGN KEY (owner_bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_media_assets_request FOREIGN KEY (design_request_id) REFERENCES design_requests (id),
  -- Ảnh mẫu bánh: (cake_id, owner_bakery_id) phải khớp cakes(id, bakery_id).
  CONSTRAINT fk_media_assets_cake_bakery FOREIGN KEY (cake_id, owner_bakery_id)
    REFERENCES cakes (id, bakery_id),
  -- Ảnh đề xuất của tiệm: tiệm phải là tiệm đã nhận yêu cầu.
  -- (FK ghép bỏ qua dòng có cột NULL, nên không ảnh hưởng ảnh của khách.)
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
-- 8. quotes — báo giá của một tiệm cho một yêu cầu; MỖI DÒNG LÀ MỘT PHIÊN BẢN
--
--    submitted -> accepted | not_selected | withdrawn | superseded
--    * Nội dung một phiên bản không bao giờ bị sửa. Sửa báo giá = sp_revise_quote:
--      phiên bản hiện hành -> 'superseded', thêm dòng mới revision_no + 1.
--    * current_slot = 1 khi phiên bản đang hiệu lực (submitted/accepted), NULL khi không.
--      UNIQUE(design_request_id, bakery_id, current_slot) => mỗi cặp yêu cầu–tiệm có
--      TỐI ĐA MỘT phiên bản hiện hành, kể cả khi có code ghi thẳng vào bảng.
--    * "Hết hạn" không phải trạng thái lưu: hết hạn khi valid_until <= thời điểm kiểm
--      tra (sp_accept_quote kiểm tra sau khi đã giữ khóa).
--    * confirmed_design_asset_id: ảnh thiết kế đi kèm phiên bản này (có thể NULL).
--      FK ghép bảo đảm ảnh thuộc cùng yêu cầu; quy tắc "ảnh đề xuất phải của chính
--      tiệm báo giá" được kiểm tra trong sp_create_quote / sp_revise_quote /
--      sp_accept_quote (xem docs, mục 7).
-- -----------------------------------------------------------------------------
CREATE TABLE quotes (
  id                         BIGINT            NOT NULL AUTO_INCREMENT,
  design_request_id          BIGINT            NOT NULL,
  bakery_id                  BIGINT            NOT NULL,
  revision_no                INT UNSIGNED      NOT NULL DEFAULT 1,
  price                      DECIMAL(12,2)     NOT NULL,
  currency                   CHAR(3)           NOT NULL DEFAULT 'VND',
  size_description           VARCHAR(100)      NULL,       -- kích thước tiệm cam kết (v2)
  servings                   SMALLINT UNSIGNED NULL,       -- số phần ăn (v2)
  flavor                     VARCHAR(200)      NOT NULL,   -- hương vị (v2)
  decoration_description     TEXT              NOT NULL,   -- mô tả trang trí (v2)
  inscription_text           VARCHAR(200)      NULL,       -- chữ trên bánh (v2)
  note                       TEXT              NULL,
  estimated_pickup_at        DATETIME(3)       NOT NULL,   -- UTC
  valid_until                DATETIME(3)       NOT NULL,   -- UTC
  confirmed_design_asset_id  BIGINT            NULL,
  created_by_user_id         BIGINT            NOT NULL,
  status                     VARCHAR(20)       NOT NULL DEFAULT 'submitted',
  current_slot               TINYINT           GENERATED ALWAYS AS (
                               CASE WHEN status IN ('submitted', 'accepted') THEN 1 ELSE NULL END
                             ) STORED,
  withdrawn_at               DATETIME(3)       NULL,
  created_at                 DATETIME(3)       NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at                 DATETIME(3)       NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_quotes PRIMARY KEY (id),
  CONSTRAINT uq_quotes_revision UNIQUE (design_request_id, bakery_id, revision_no),
  CONSTRAINT uq_quotes_current UNIQUE (design_request_id, bakery_id, current_slot),
  -- Đích của FK ghép từ orders: đơn phải khớp yêu cầu + tiệm của báo giá.
  CONSTRAINT uq_quotes_id_request_bakery UNIQUE (id, design_request_id, bakery_id),
  -- Chỉ tiệm đã nhận yêu cầu mới báo giá được.
  CONSTRAINT fk_quotes_request_bakery FOREIGN KEY (design_request_id, bakery_id)
    REFERENCES request_bakeries (design_request_id, bakery_id),
  -- Người tạo phiên bản phải là thành viên (đã từng) của tiệm.
  CONSTRAINT fk_quotes_created_by_member FOREIGN KEY (bakery_id, created_by_user_id)
    REFERENCES bakery_members (bakery_id, user_id),
  -- Ảnh thiết kế phải gắn với cùng yêu cầu (loại bỏ cả cake_photo vì cột đó NULL).
  CONSTRAINT fk_quotes_confirmed_design FOREIGN KEY (confirmed_design_asset_id, design_request_id)
    REFERENCES media_assets (id, design_request_id),
  CONSTRAINT chk_quotes_price CHECK (price > 0),
  CONSTRAINT chk_quotes_currency CHECK (currency IN ('VND')),
  CONSTRAINT chk_quotes_revision CHECK (revision_no >= 1),
  CONSTRAINT chk_quotes_size CHECK (size_description IS NOT NULL OR servings IS NOT NULL),
  CONSTRAINT chk_quotes_servings CHECK (servings IS NULL OR servings > 0),
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
--  * UNIQUE(design_request_id): một yêu cầu tạo tối đa một đơn (giả định v1, giữ ở v2).
--  * Các cột agreed_* là BẢN CHỐT sao chép từ phiên bản báo giá (và ảnh) tại lúc khách
--    chọn. Sửa yêu cầu / ảnh / báo giá về sau không làm thay đổi đơn. Stored procedure
--    không bao giờ UPDATE các cột này; tài khoản MySQL của ứng dụng không có quyền
--    UPDATE bảng orders (security/app_user_grants.sql).
-- -----------------------------------------------------------------------------
CREATE TABLE orders (
  id                             BIGINT            NOT NULL AUTO_INCREMENT,
  quote_id                       BIGINT            NOT NULL,
  design_request_id              BIGINT            NOT NULL,
  customer_id                    BIGINT            NOT NULL,
  bakery_id                      BIGINT            NOT NULL,
  -- ---- Bản chốt ----
  agreed_quote_revision_no       INT UNSIGNED      NOT NULL,
  agreed_price                   DECIMAL(12,2)     NOT NULL,
  currency                       CHAR(3)           NOT NULL DEFAULT 'VND',
  agreed_pickup_at               DATETIME(3)       NOT NULL,   -- UTC
  agreed_size_description        VARCHAR(100)      NULL,
  agreed_servings                SMALLINT UNSIGNED NULL,
  agreed_flavor                  VARCHAR(200)      NOT NULL,
  agreed_decoration_description  TEXT              NOT NULL,
  agreed_inscription_text        VARCHAR(200)      NULL,
  agreed_note                    TEXT              NULL,
  agreed_design_asset_id         BIGINT            NULL,
  agreed_design_asset_kind       VARCHAR(30)       NULL,
  agreed_design_version_no       INT UNSIGNED      NULL,
  agreed_design_url              VARCHAR(1024)     NULL,
  -- ---- Trạng thái ----
  status                         VARCHAR(30)       NOT NULL DEFAULT 'pending_confirmation',
  bakery_decided_at              DATETIME(3)       NULL,
  bakery_decided_by_user_id      BIGINT            NULL,
  rejection_reason               VARCHAR(500)      NULL,
  picked_up_at                   DATETIME(3)       NULL,
  cancelled_at                   DATETIME(3)       NULL,
  created_at                     DATETIME(3)       NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  updated_at                     DATETIME(3)       NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_orders PRIMARY KEY (id),
  CONSTRAINT uq_orders_quote UNIQUE (quote_id),
  CONSTRAINT uq_orders_design_request UNIQUE (design_request_id),
  CONSTRAINT fk_orders_quote FOREIGN KEY (quote_id, design_request_id, bakery_id)
    REFERENCES quotes (id, design_request_id, bakery_id),
  CONSTRAINT fk_orders_request_customer FOREIGN KEY (design_request_id, customer_id)
    REFERENCES design_requests (id, customer_id),
  CONSTRAINT fk_orders_bakery FOREIGN KEY (bakery_id) REFERENCES bakeries (id),
  CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES users (id),
  CONSTRAINT fk_orders_decided_by_member FOREIGN KEY (bakery_id, bakery_decided_by_user_id)
    REFERENCES bakery_members (bakery_id, user_id),
  CONSTRAINT fk_orders_agreed_design FOREIGN KEY (agreed_design_asset_id) REFERENCES media_assets (id),
  CONSTRAINT chk_orders_status CHECK (status IN (
    'pending_confirmation', 'confirmed', 'rejected_by_bakery', 'ready', 'picked_up', 'cancelled'
  )),
  CONSTRAINT chk_orders_price CHECK (agreed_price > 0),
  CONSTRAINT chk_orders_currency CHECK (currency IN ('VND')),
  CONSTRAINT chk_orders_agreed_size
    CHECK (agreed_size_description IS NOT NULL OR agreed_servings IS NOT NULL),
  CONSTRAINT chk_orders_agreed_design CHECK (
       (agreed_design_asset_id IS NULL AND agreed_design_url IS NULL AND agreed_design_asset_kind IS NULL)
    OR (agreed_design_asset_id IS NOT NULL AND agreed_design_url IS NOT NULL
        AND agreed_design_asset_kind IN ('customer_reference', 'ai_generated', 'design_proposal'))
  ),
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

-- -----------------------------------------------------------------------------
-- Ghi phiên bản schema
-- -----------------------------------------------------------------------------
INSERT INTO schema_migrations (version, description) VALUES
  (1, 'Schema v1: 9 bảng lõi'),
  (2, 'Schema v2: bản chốt đơn, nội dung báo giá, phiên bản báo giá hiện hành duy nhất');
