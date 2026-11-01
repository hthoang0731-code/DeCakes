-- =============================================================================
-- DeCakes — migrations/V005__subscription_payments.grants.sql   (ĐỀ XUẤT, chưa áp dụng)
--
-- Quyền bổ sung cho tài khoản backend sau V005. Lặp lại cho mọi tài khoản backend.
-- Không DELETE. EXECUTE trên sp_guard_bakery_entitled đã có qua GRANT EXECUTE ON decakes_db.*;
-- backend không gọi guard trực tiếp. Kỳ trả phí dùng quyền INSERT đã cấp ở V004.
-- =============================================================================

GRANT INSERT, UPDATE ON decakes_db.subscription_payments TO 'decakes_app'@'%';
