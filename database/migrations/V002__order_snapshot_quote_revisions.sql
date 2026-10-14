-- =============================================================================
-- DeCakes — migrations/V002__order_snapshot_quote_revisions.sql
--
-- Nâng cấp database `decakes_db` ĐÃ CÓ schema v1 (9 bảng, chưa có schema_migrations)
-- lên schema v2, GIỮ NGUYÊN dữ liệu hiện có.
--
-- Không dùng cho database trống — database trống dùng install/01_schema.sql.
--
-- TRƯỚC KHI CHẠY:
--   * Sao lưu. Aiven: Console -> service -> Backups (hoặc Fork service để thử trước).
--     DBeaver: chuột phải database -> Tools -> Dump database.
--   * DDL của MySQL không nằm trong transaction: mỗi ALTER TABLE là nguyên tử, nhưng cả
--     file thì không. Nếu file dừng giữa chừng, KHÔNG chạy lại; khôi phục bản sao lưu,
--     sửa nguyên nhân rồi chạy lại từ đầu. Phần kiểm tra đầu file sẽ từ chối chạy trên
--     schema đã bị đổi một phần.
--
-- SAU KHI CHẠY: chạy install/02_procedures.sql để thay thủ tục v1 bằng thủ tục v2.
--
-- Thay đổi:
--   1. design_requests: thêm inscription_text.
--   2. quotes: thêm nội dung báo giá (kích thước/số phần, hương vị, trang trí, chữ);
--      thêm current_slot + UNIQUE uq_quotes_current (một phiên bản hiện hành).
--   3. orders: đổi pickup_at -> agreed_pickup_at; thêm các cột bản chốt agreed_*.
--   4. Tạo schema_migrations, ghi phiên bản 1 và 2.
--
-- Điền dữ liệu cho bản ghi cũ (v1 không lưu các thông tin này):
--   * quotes v1: kích thước, số phần, hương vị lấy từ yêu cầu; mô tả trang trí lấy từ
--     idea_description của yêu cầu. Thiếu dữ liệu thì ghi nhãn "[v1: chưa ghi ...]".
--   * orders v1: bản chốt chép từ báo giá đã chọn (sau bước trên) và ảnh của báo giá,
--     TẠI THỜI ĐIỂM MIGRATION. v1 chưa có bản chốt nên đây là thông tin tốt nhất có.
--     Giá và ngày nhận giữ nguyên giá trị đơn v1 đã lưu.
-- =============================================================================

SET NAMES utf8mb4;
SET SQL_SAFE_UPDATES = 0;

USE decakes_db;

-- -----------------------------------------------------------------------------
-- 0. Kiểm tra điều kiện trước khi đổi bất cứ thứ gì
-- -----------------------------------------------------------------------------
DROP PROCEDURE IF EXISTS tmp_v002_precheck;

DELIMITER $$

CREATE PROCEDURE tmp_v002_precheck()
BEGIN
  DECLARE v_tables   INT DEFAULT 0;
  DECLARE v_has_sm   INT DEFAULT 0;
  DECLARE v_v1_shape INT DEFAULT 0;
  DECLARE v_dup      INT DEFAULT 0;
  SELECT COUNT(*) INTO v_tables
  FROM information_schema.tables
  WHERE table_schema = DATABASE()
    AND table_name IN ('users', 'bakeries', 'bakery_members', 'cakes', 'design_requests',
                       'request_bakeries', 'media_assets', 'quotes', 'orders');
  IF v_tables <> 9 THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'MIGRATION_V002_NOT_V1_SCHEMA: thiếu bảng v1 (database trống thì dùng install/01_schema.sql)';
  END IF;
  SELECT COUNT(*) INTO v_has_sm
  FROM information_schema.tables
  WHERE table_schema = DATABASE() AND table_name = 'schema_migrations';
  IF v_has_sm > 0 THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'MIGRATION_V002_ALREADY_APPLIED: đã có bảng schema_migrations (schema v2 hoặc đã chạy migration)';
  END IF;
  -- Đúng hình dạng v1: orders còn pickup_at, quotes chưa có flavor/current_slot,
  -- design_requests chưa có inscription_text, orders chưa có agreed_flavor.
  SELECT
      (SELECT COUNT(*) FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'orders' AND column_name = 'pickup_at')
    + (SELECT COUNT(*) = 0 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'quotes'
          AND column_name IN ('flavor', 'current_slot', 'size_description', 'decoration_description'))
    + (SELECT COUNT(*) = 0 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'design_requests' AND column_name = 'inscription_text')
    + (SELECT COUNT(*) = 0 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'orders' AND column_name LIKE 'agreed\\_%' AND column_name <> 'agreed_price')
    INTO v_v1_shape;
  IF v_v1_shape <> 4 THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'MIGRATION_V002_UNEXPECTED_SCHEMA: schema không đúng v1 (có thể migration đã chạy dở) — khôi phục bản sao lưu';
  END IF;
  -- Dữ liệu phải thỏa ràng buộc mới: mỗi cặp yêu cầu–tiệm tối đa một báo giá hiện hành.
  SELECT COUNT(*) INTO v_dup FROM (
    SELECT design_request_id, bakery_id
    FROM quotes
    WHERE status IN ('submitted', 'accepted')
    GROUP BY design_request_id, bakery_id
    HAVING COUNT(*) > 1
  ) d;
  IF v_dup > 0 THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'MIGRATION_V002_DUPLICATE_CURRENT_QUOTES: có cặp yêu cầu–tiệm với hơn một báo giá submitted/accepted; chuyển bản cũ sang superseded rồi chạy lại';
  END IF;
END$$

DELIMITER ;

CALL tmp_v002_precheck();
DROP PROCEDURE tmp_v002_precheck;

-- -----------------------------------------------------------------------------
-- 1. design_requests
-- -----------------------------------------------------------------------------
ALTER TABLE design_requests
  ADD COLUMN inscription_text VARCHAR(200) NULL AFTER flavor;

-- -----------------------------------------------------------------------------
-- 2. quotes — thêm cột (cho phép NULL), điền dữ liệu, rồi siết ràng buộc
-- -----------------------------------------------------------------------------
ALTER TABLE quotes
  ADD COLUMN size_description       VARCHAR(100)      NULL AFTER currency,
  ADD COLUMN servings               SMALLINT UNSIGNED NULL AFTER size_description,
  ADD COLUMN flavor                 VARCHAR(200)      NULL AFTER servings,
  ADD COLUMN decoration_description TEXT              NULL AFTER flavor,
  ADD COLUMN inscription_text       VARCHAR(200)      NULL AFTER decoration_description;

UPDATE quotes q
JOIN design_requests r ON r.id = q.design_request_id
SET q.size_description = CASE
                           WHEN r.size_description IS NOT NULL THEN r.size_description
                           WHEN r.servings IS NULL THEN '[v1: chưa ghi kích thước]'
                           ELSE NULL
                         END,
    q.servings         = r.servings,
    q.flavor           = COALESCE(NULLIF(TRIM(r.flavor), ''), '[v1: chưa ghi hương vị]'),
    q.decoration_description = r.idea_description;

ALTER TABLE quotes
  MODIFY COLUMN flavor                 VARCHAR(200) NOT NULL,
  MODIFY COLUMN decoration_description TEXT         NOT NULL,
  ADD COLUMN current_slot TINYINT GENERATED ALWAYS AS (
    CASE WHEN status IN ('submitted', 'accepted') THEN 1 ELSE NULL END
  ) STORED AFTER status,
  ADD CONSTRAINT uq_quotes_current UNIQUE (design_request_id, bakery_id, current_slot),
  ADD CONSTRAINT chk_quotes_size CHECK (size_description IS NOT NULL OR servings IS NOT NULL),
  ADD CONSTRAINT chk_quotes_servings CHECK (servings IS NULL OR servings > 0);

-- -----------------------------------------------------------------------------
-- 3. orders — đổi tên, thêm cột bản chốt, điền dữ liệu, siết ràng buộc
-- -----------------------------------------------------------------------------
ALTER TABLE orders
  RENAME COLUMN pickup_at TO agreed_pickup_at;

ALTER TABLE orders
  ADD COLUMN agreed_quote_revision_no      INT UNSIGNED      NULL AFTER bakery_id,
  ADD COLUMN agreed_size_description       VARCHAR(100)      NULL AFTER agreed_pickup_at,
  ADD COLUMN agreed_servings               SMALLINT UNSIGNED NULL AFTER agreed_size_description,
  ADD COLUMN agreed_flavor                 VARCHAR(200)      NULL AFTER agreed_servings,
  ADD COLUMN agreed_decoration_description TEXT              NULL AFTER agreed_flavor,
  ADD COLUMN agreed_inscription_text       VARCHAR(200)      NULL AFTER agreed_decoration_description,
  ADD COLUMN agreed_note                   TEXT              NULL AFTER agreed_inscription_text,
  ADD COLUMN agreed_design_asset_id        BIGINT            NULL AFTER agreed_note,
  ADD COLUMN agreed_design_asset_kind      VARCHAR(30)       NULL AFTER agreed_design_asset_id,
  ADD COLUMN agreed_design_version_no      INT UNSIGNED      NULL AFTER agreed_design_asset_kind,
  ADD COLUMN agreed_design_url             VARCHAR(1024)     NULL AFTER agreed_design_version_no;

UPDATE orders o
JOIN quotes q ON q.id = o.quote_id
LEFT JOIN media_assets m ON m.id = q.confirmed_design_asset_id
SET o.agreed_quote_revision_no      = q.revision_no,
    o.agreed_size_description       = q.size_description,
    o.agreed_servings               = q.servings,
    o.agreed_flavor                 = q.flavor,
    o.agreed_decoration_description = q.decoration_description,
    o.agreed_inscription_text       = q.inscription_text,
    o.agreed_note                   = q.note,
    o.agreed_design_asset_id        = m.id,
    o.agreed_design_asset_kind      = m.asset_kind,
    o.agreed_design_version_no      = m.version_no,
    o.agreed_design_url             = m.storage_url;

ALTER TABLE orders
  MODIFY COLUMN agreed_quote_revision_no      INT UNSIGNED NOT NULL,
  MODIFY COLUMN agreed_flavor                 VARCHAR(200) NOT NULL,
  MODIFY COLUMN agreed_decoration_description TEXT         NOT NULL,
  ADD CONSTRAINT fk_orders_agreed_design FOREIGN KEY (agreed_design_asset_id) REFERENCES media_assets (id),
  ADD CONSTRAINT chk_orders_agreed_size
    CHECK (agreed_size_description IS NOT NULL OR agreed_servings IS NOT NULL),
  ADD CONSTRAINT chk_orders_agreed_design CHECK (
       (agreed_design_asset_id IS NULL AND agreed_design_url IS NULL AND agreed_design_asset_kind IS NULL)
    OR (agreed_design_asset_id IS NOT NULL AND agreed_design_url IS NOT NULL
        AND agreed_design_asset_kind IN ('customer_reference', 'ai_generated', 'design_proposal'))
  );

-- -----------------------------------------------------------------------------
-- 4. schema_migrations
-- -----------------------------------------------------------------------------
CREATE TABLE schema_migrations (
  version      INT           NOT NULL,
  description  VARCHAR(200)  NOT NULL,
  applied_at   DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),

  CONSTRAINT pk_schema_migrations PRIMARY KEY (version)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

INSERT INTO schema_migrations (version, description) VALUES
  (1, 'Schema v1: 9 bảng lõi'),
  (2, 'Schema v2: bản chốt đơn, nội dung báo giá, phiên bản báo giá hiện hành duy nhất');

-- Kết quả
SELECT version, description, applied_at FROM schema_migrations ORDER BY version;
SELECT COUNT(*) AS orders_total,
       SUM(agreed_flavor IS NOT NULL) AS orders_with_snapshot
FROM orders;
