-- =============================================================================
-- DeCakes — tests/20_concurrency_setup.sql
--
-- Chuẩn bị dữ liệu cho kiểm thử NHIỀU connection làm THỦ CÔNG trong DBeaver
-- (hướng dẫn từng bước: database/README.md, mục "Kiểm thử nhiều connection").
-- Bản tự động của cùng các kịch bản: tests/run_tests.py.
--
-- Chạy sau install/03_seed_demo.sql, trên database thử nghiệm. Kết quả cuối là một
-- bảng ID cần dùng cho các bước tiếp theo.
--
-- Báo giá của kịch bản 1 hết hạn sau 90 giây kể từ lúc chạy file này: làm các bước
-- của kịch bản 1 ngay sau khi chạy (hoặc chạy lại file để có dữ liệu mới).
-- =============================================================================

SET NAMES utf8mb4;
SET SQL_SAFE_UPDATES = 0;

USE decakes_db;

SELECT id INTO @c1      FROM users    WHERE email = 'khach.demo@demo.decakes.invalid';
SELECT id INTO @owner_a FROM users    WHERE email = 'chu.tiem.a@demo.decakes.invalid';
SELECT id INTO @owner_b FROM users    WHERE email = 'chu.tiem.b@demo.decakes.invalid';
SELECT id INTO @bk_a    FROM bakeries WHERE slug  = 'demo-tiem-banh-a';
SELECT id INTO @bk_b    FROM bakeries WHERE slug  = 'demo-tiem-banh-b';
SET @far = UTC_TIMESTAMP(3) + INTERVAL 10 DAY;

-- Kịch bản 1: báo giá hết hạn trong lúc chờ khóa (hạn 90 giây)
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Thủ công 1: hết hạn khi chờ khóa', 'x', 900000, @far, 'draft', 1);
SET @r1 = LAST_INSERT_ID();
CALL sp_send_request_to_bakery(@r1, @c1, @bk_a);
CALL sp_create_quote(@r1, @bk_a, @owner_a, 500000, '18 cm', 10, 'Vani', '[TEST] Trang trí', NULL, NULL,
                     @far, UTC_TIMESTAMP(3) + INTERVAL 90 SECOND, NULL, @q1);

-- Kịch bản 2: hai lần chọn đồng thời (hai báo giá của hai tiệm, cùng yêu cầu)
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Thủ công 2: chọn đồng thời', 'x', 900000, @far, 'draft', 1);
SET @r2 = LAST_INSERT_ID();
CALL sp_send_request_to_bakery(@r2, @c1, @bk_a);
CALL sp_send_request_to_bakery(@r2, @c1, @bk_b);
CALL sp_create_quote(@r2, @bk_a, @owner_a, 500000, '18 cm', 10, 'Vani', '[TEST] Trang trí', NULL, NULL,
                     @far, UTC_TIMESTAMP(3) + INTERVAL 1 DAY, NULL, @q2a);
CALL sp_create_quote(@r2, @bk_b, @owner_b, 520000, '18 cm', 10, 'Vani', '[TEST] Trang trí', NULL, NULL,
                     @far, UTC_TIMESTAMP(3) + INTERVAL 1 DAY, NULL, @q2b);

-- Kịch bản 3: hai lần sửa đồng thời cùng một phiên bản báo giá
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Thủ công 3: sửa đồng thời', 'x', 900000, @far, 'draft', 1);
SET @r3 = LAST_INSERT_ID();
CALL sp_send_request_to_bakery(@r3, @c1, @bk_a);
CALL sp_create_quote(@r3, @bk_a, @owner_a, 500000, '18 cm', 10, 'Vani', '[TEST] Trang trí', NULL, NULL,
                     @far, UTC_TIMESTAMP(3) + INTERVAL 1 DAY, NULL, @q3);

SELECT 'Kịch bản 1' AS scenario, @r1 AS request_id, @q1  AS quote_id, NULL AS quote_id_2, @c1 AS customer_id,
       @owner_a AS bakery_user_id, (SELECT valid_until FROM quotes WHERE id = @q1) AS valid_until_utc
UNION ALL
SELECT 'Kịch bản 2', @r2, @q2a, @q2b, @c1, NULL, NULL
UNION ALL
SELECT 'Kịch bản 3', @r3, @q3, NULL, NULL, @owner_a, NULL;
