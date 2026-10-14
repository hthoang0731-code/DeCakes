-- =============================================================================
-- DeCakes — install/00_create_database_local.sql
--
-- CHỈ dùng cho MySQL trên máy phát triển. KHÔNG chạy trên Aiven: database
-- `decakes_db` đã được tạo trên Aiven Console.
--
-- Không có DROP DATABASE. Nếu database đã tồn tại, lệnh báo lỗi
-- "Can't create database 'decakes_db'; database exists" — đó là chủ ý.
-- =============================================================================

CREATE DATABASE decakes_db
  CHARACTER SET utf8mb4
  COLLATE utf8mb4_0900_ai_ci;
