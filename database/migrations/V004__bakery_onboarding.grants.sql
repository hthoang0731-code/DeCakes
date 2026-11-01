-- =============================================================================
-- DeCakes — migrations/V004__bakery_onboarding.grants.sql   (ĐỀ XUẤT, chưa áp dụng)
--
-- Quyền bổ sung cho tài khoản backend sau V004. Lặp lại cho mọi tài khoản backend.
-- subscription_plans: backend chỉ đọc (SELECT đã có trên decakes_db.*); người vận hành quản lý.
-- Bảng lịch sử (review events, periods) chỉ INSERT: không sửa/xóa lịch sử từ backend.
-- =============================================================================

GRANT INSERT ON decakes_db.bakery_review_events TO 'decakes_app'@'%';
GRANT INSERT, UPDATE ON decakes_db.bakery_subscriptions TO 'decakes_app'@'%';
GRANT INSERT ON decakes_db.bakery_subscription_periods TO 'decakes_app'@'%';
GRANT INSERT, UPDATE ON decakes_db.bakery_invitations TO 'decakes_app'@'%';
