-- =============================================================================
-- DeCakes — migrations/V003__account_security.sql   (ĐỀ XUẤT, chưa áp dụng)
--
-- Bổ sung dữ liệu cho xác minh email, quên/đặt lại/đổi mật khẩu và thu hồi phiên.
-- Áp dụng cho database đã ở schema v2 (install/01 + 02, hoặc sau V002).
--
-- KHÔNG tự chạy lên Aiven. Đây là đề xuất cần thống nhất riêng; chỉ đã được nạp trên
-- MySQL 8.4 kiểm thử dùng một lần (fixture tests/DeCakes.Infrastructure.Tests).
--
-- TRƯỚC KHI CHẠY: sao lưu. DDL của MySQL không nằm trong transaction; nếu file dừng giữa
-- chừng, khôi phục bản sao lưu rồi chạy lại từ đầu (phần kiểm tra đầu file từ chối
-- chạy trên schema đã có version 3).
-- SAU KHI CHẠY: cấp quyền theo V003__account_security.grants.sql cho tài khoản backend
-- (decakes_app và mọi tài khoản backend khác, vd. decakes_app_member2).
--
-- Thay đổi:
--   1. users.email_verified_at: NULL = chưa xác minh. Tài khoản đã có GIỮ NULL (không tự
--      đánh dấu đã xác minh); họ vẫn đăng nhập được và tự xác minh qua email.
--   2. users.session_version: tăng khi đổi/đặt lại mật khẩu; cookie mang giá trị lúc cấp,
--      lệch giá trị thì cookie bị từ chối ở mọi trình duyệt.
--   3. user_tokens: token xác minh email / đặt lại mật khẩu. Chỉ lưu SHA-256 của token
--      (token thô chỉ có trong email), có mục đích, hạn, dùng một lần, thu hồi được.
--      Không xóa dòng; consumed_at / revoked_at ghi lại kết cục.
--   4. schema_migrations: version 3.
-- =============================================================================

SET NAMES utf8mb4;

USE decakes_db;

DROP PROCEDURE IF EXISTS tmp_v003_precheck;

DELIMITER $$

CREATE PROCEDURE tmp_v003_precheck()
BEGIN
  DECLARE v_v2 INT DEFAULT 0;
  DECLARE v_v3 INT DEFAULT 0;
  SELECT COUNT(*) INTO v_v2 FROM schema_migrations WHERE version = 2;
  SELECT COUNT(*) INTO v_v3 FROM schema_migrations WHERE version = 3;
  IF v_v2 <> 1 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V003_REQUIRES_V2';
  END IF;
  IF v_v3 <> 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'MIGRATION_V003_ALREADY_APPLIED';
  END IF;
END$$

DELIMITER ;

CALL tmp_v003_precheck();

DROP PROCEDURE tmp_v003_precheck;

ALTER TABLE users
  ADD COLUMN email_verified_at DATETIME(3)  NULL AFTER password_hash,
  ADD COLUMN session_version   INT UNSIGNED NOT NULL DEFAULT 0 AFTER email_verified_at;

CREATE TABLE user_tokens (
  id           BIGINT        NOT NULL AUTO_INCREMENT,
  user_id      BIGINT        NOT NULL,
  purpose      VARCHAR(30)   NOT NULL,
  -- SHA-256 của 32 byte ngẫu nhiên; không lưu token thô.
  token_hash   BINARY(32)    NOT NULL,
  -- Email của user lúc cấp; token mất hiệu lực nếu email của user khác giá trị này.
  email        VARCHAR(255)  NOT NULL,
  created_at   DATETIME(3)   NOT NULL DEFAULT (UTC_TIMESTAMP(3)),
  expires_at   DATETIME(3)   NOT NULL,
  consumed_at  DATETIME(3)   NULL,
  revoked_at   DATETIME(3)   NULL,

  CONSTRAINT pk_user_tokens PRIMARY KEY (id),
  CONSTRAINT uq_user_tokens_hash UNIQUE (token_hash),
  CONSTRAINT fk_user_tokens_user FOREIGN KEY (user_id) REFERENCES users (id),
  CONSTRAINT chk_user_tokens_purpose CHECK (purpose IN ('email_verification', 'password_reset')),
  CONSTRAINT chk_user_tokens_expiry CHECK (expires_at > created_at),
  CONSTRAINT chk_user_tokens_outcome CHECK (consumed_at IS NULL OR revoked_at IS NULL),
  INDEX ix_user_tokens_user_purpose (user_id, purpose, created_at)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_0900_ai_ci;

INSERT INTO schema_migrations (version, description)
VALUES (3, 'account security: email verification, password tokens, session version');
