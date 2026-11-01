-- =============================================================================
-- DeCakes — migrations/V003__account_security.grants.sql   (ĐỀ XUẤT, chưa áp dụng)
--
-- Quyền bổ sung cho tài khoản backend sau V003__account_security.sql. Chạy bằng tài khoản
-- quản trị, cùng đợt với migration. Lặp lại cho mọi tài khoản backend (vd. thay
-- 'decakes_app' bằng 'decakes_app_member2'). Không có DELETE: token chỉ được đánh dấu
-- consumed/revoked. Cột mới của users dùng quyền INSERT, UPDATE đã có trên users.
-- =============================================================================

GRANT INSERT, UPDATE ON decakes_db.user_tokens TO 'decakes_app'@'%';
