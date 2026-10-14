-- =============================================================================
-- DeCakes — tests/10_single_connection_tests.sql   (kiểm thử một connection, v2)
--
-- Mỗi test cố ý làm một thao tác SAI và mong database từ chối đúng mã lỗi / đúng tên
-- ràng buộc, hoặc làm thao tác ĐÚNG và kiểm tra kết quả.
--
-- Chạy sau install/03_seed_demo.sql (cần các tài khoản/tiệm demo). Script thêm dữ liệu
-- "[TEST]" (is_demo = 1) vào database; mỗi lần chạy dùng email/slug mới nên chạy lại
-- được. KHÔNG chạy trên database production — dùng database thử (local hoặc fork).
--
-- Kết quả: bảng PASS/FAIL và dòng tổng kết. Dòng tên bắt đầu "INFO" chỉ để tham khảo.
-- Mất khoảng 5 giây (có SLEEP để kiểm tra hạn báo giá theo thời gian thực).
--
-- Kiểm thử NHIỀU connection (chờ khóa, chọn/sửa đồng thời) và migration nằm ở
-- tests/run_tests.py và database/README.md.
-- =============================================================================

SET NAMES utf8mb4;
SET SQL_SAFE_UPDATES = 0;

USE decakes_db;

DROP TEMPORARY TABLE IF EXISTS test_results;
CREATE TEMPORARY TABLE test_results (
  seq        INT AUTO_INCREMENT PRIMARY KEY,
  test_name  VARCHAR(200) NOT NULL,
  passed     TINYINT(1)   NOT NULL,
  detail     TEXT         NULL
);

DROP PROCEDURE IF EXISTS t_expect_error;
DROP PROCEDURE IF EXISTS t_expect_ok;
DROP PROCEDURE IF EXISTS t_assert;
DROP PROCEDURE IF EXISTS t_quote;
DROP PROCEDURE IF EXISTS t_revise;
DROP PROCEDURE IF EXISTS t_sleep_then_accept;

DELIMITER $$

-- Chạy p_sql; PASS nếu có lỗi và thông báo lỗi chứa p_expect.
CREATE PROCEDURE t_expect_error(IN p_name VARCHAR(200), IN p_sql TEXT, IN p_expect VARCHAR(200))
BEGIN
  DECLARE v_err TEXT DEFAULT NULL;
  DECLARE v_tmp TEXT;
  DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
  BEGIN
    GET DIAGNOSTICS CONDITION 1 v_tmp = MESSAGE_TEXT;
    IF v_err IS NULL THEN SET v_err = v_tmp; END IF;
  END;
  SET @t_sql = p_sql;
  PREPARE t_stmt FROM @t_sql;
  EXECUTE t_stmt;
  DEALLOCATE PREPARE t_stmt;
  INSERT INTO test_results (test_name, passed, detail)
  VALUES (p_name,
          v_err IS NOT NULL AND LOCATE(p_expect, v_err) > 0,
          COALESCE(v_err, CONCAT('Không có lỗi, mong đợi: ', p_expect)));
END$$

-- Chạy p_sql; PASS nếu không có lỗi.
CREATE PROCEDURE t_expect_ok(IN p_name VARCHAR(200), IN p_sql TEXT)
BEGIN
  DECLARE v_err TEXT DEFAULT NULL;
  DECLARE v_tmp TEXT;
  DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
  BEGIN
    GET DIAGNOSTICS CONDITION 1 v_tmp = MESSAGE_TEXT;
    IF v_err IS NULL THEN SET v_err = v_tmp; END IF;
  END;
  SET @t_sql = p_sql;
  PREPARE t_stmt FROM @t_sql;
  EXECUTE t_stmt;
  DEALLOCATE PREPARE t_stmt;
  INSERT INTO test_results (test_name, passed, detail)
  VALUES (p_name, v_err IS NULL, COALESCE(v_err, 'OK'));
END$$

CREATE PROCEDURE t_assert(IN p_name VARCHAR(200), IN p_condition TINYINT, IN p_detail TEXT)
BEGIN
  INSERT INTO test_results (test_name, passed, detail)
  VALUES (p_name, COALESCE(p_condition, 0), p_detail);
END$$

-- Tạo báo giá với nội dung mặc định; id mới ở @last_quote.
CREATE PROCEDURE t_quote(
  IN p_request BIGINT, IN p_bakery BIGINT, IN p_actor BIGINT, IN p_asset BIGINT, IN p_valid_seconds INT
)
BEGIN
  SET @last_quote = NULL;
  CALL sp_create_quote(p_request, p_bakery, p_actor,
    500000.00, '18 cm', 10, 'Vani', '[TEST] Trang trí mặc định', NULL, NULL,
    UTC_TIMESTAMP(3) + INTERVAL 10 DAY, UTC_TIMESTAMP(3) + INTERVAL p_valid_seconds SECOND,
    p_asset, @last_quote);
END$$

-- Sửa báo giá với nội dung mặc định; id phiên bản mới ở @last_quote.
CREATE PROCEDURE t_revise(
  IN p_quote BIGINT, IN p_actor BIGINT, IN p_asset BIGINT, IN p_price DECIMAL(12,2), IN p_valid_seconds INT
)
BEGIN
  SET @last_quote = NULL;
  CALL sp_revise_quote(p_quote, p_actor,
    p_price, '20 cm', 14, 'Sô-cô-la', '[TEST] Trang trí đã sửa', 'Chữ mới', '[TEST] Ghi chú sửa',
    UTC_TIMESTAMP(3) + INTERVAL 10 DAY, UTC_TIMESTAMP(3) + INTERVAL p_valid_seconds SECOND,
    p_asset, @last_quote);
END$$

-- Ngủ rồi mới chọn báo giá, TRONG CÙNG một câu lệnh CALL ngoài cùng. Nếu thủ tục kiểm
-- tra hạn bằng NOW()/UTC_TIMESTAMP() (thời điểm bắt đầu câu lệnh), báo giá đã hết hạn
-- trong lúc ngủ có thể vẫn được chấp nhận. Ghi lại NOW() trước/sau để tham khảo.
CREATE PROCEDURE t_sleep_then_accept(IN p_quote BIGINT, IN p_customer BIGINT, IN p_seconds INT)
BEGIN
  SET @t_now_before = NOW(3);
  DO SLEEP(p_seconds);
  SET @t_now_after = NOW(3);
  CALL sp_accept_quote(p_quote, p_customer, @o);
END$$

DELIMITER ;

-- =============================================================================
-- Chuẩn bị dữ liệu
-- =============================================================================
SET @now = UTC_TIMESTAMP(3);
SET @tag = LEFT(REPLACE(UUID(), '-', ''), 12);
SET @far = DATE_ADD(@now, INTERVAL 10 DAY);

SELECT id INTO @c1      FROM users    WHERE email = 'khach.demo@demo.decakes.invalid';
SELECT id INTO @owner_a FROM users    WHERE email = 'chu.tiem.a@demo.decakes.invalid';
SELECT id INTO @owner_b FROM users    WHERE email = 'chu.tiem.b@demo.decakes.invalid';
SELECT id INTO @bk_a    FROM bakeries WHERE slug  = 'demo-tiem-banh-a';
SELECT id INTO @bk_b    FROM bakeries WHERE slug  = 'demo-tiem-banh-b';

-- Người dùng thêm
INSERT INTO users (email, full_name, system_role, is_demo)
VALUES (CONCAT('khach2.', @tag, '@demo.decakes.invalid'), '[TEST] Khách thứ hai', 'customer', 1);
SET @c2 = LAST_INSERT_ID();
INSERT INTO users (email, full_name, system_role, is_demo)
VALUES (CONCAT('khach3.', @tag, '@demo.decakes.invalid'), '[TEST] Khách sẽ bị khóa', 'customer', 1);
SET @c3 = LAST_INSERT_ID();
INSERT INTO users (email, full_name, system_role, is_demo)
VALUES (CONCAT('nv.a2.', @tag, '@demo.decakes.invalid'), '[TEST] Nhân viên A sẽ bị removed', 'bakery', 1);
SET @staff_a2 = LAST_INSERT_ID();
INSERT INTO users (email, full_name, system_role, status, is_demo)
VALUES (CONCAT('nv.a3.', @tag, '@demo.decakes.invalid'), '[TEST] Nhân viên A bị disabled', 'bakery', 'disabled', 1);
SET @staff_dis = LAST_INSERT_ID();
INSERT INTO users (email, full_name, system_role, is_demo)
VALUES (CONCAT('chu.c.', @tag, '@demo.decakes.invalid'), '[TEST] Chủ Tiệm C', 'bakery', 1);
SET @owner_c = LAST_INSERT_ID();
INSERT INTO users (email, full_name, system_role, is_demo)
VALUES (CONCAT('chu.d.', @tag, '@demo.decakes.invalid'), '[TEST] Chủ Tiệm D', 'bakery', 1);
SET @owner_d = LAST_INSERT_ID();

-- Tiệm thêm: C active (sẽ bị suspended ở nhóm G), D draft
INSERT INTO bakeries (name, slug, profile_status, is_demo)
VALUES ('[TEST] Tiệm C', CONCAT('test-c-', @tag), 'active', 1);
SET @bk_c = LAST_INSERT_ID();
INSERT INTO bakeries (name, slug, profile_status, is_demo)
VALUES ('[TEST] Tiệm D', CONCAT('test-d-', @tag), 'draft', 1);
SET @bk_d = LAST_INSERT_ID();

INSERT INTO bakery_members (bakery_id, user_id, member_role) VALUES
  (@bk_a, @staff_a2,  'staff'),
  (@bk_a, @staff_dis, 'staff'),
  (@bk_c, @owner_c,   'owner'),
  (@bk_d, @owner_d,   'owner');

-- R2 (dữ liệu cho nhóm A–E, tạo trực tiếp như bản v1):
--   q_ok (Tiệm A, còn hạn), q_exp (Tiệm B rev 1, đã hết hạn), q_wd (Tiệm B rev 2, đã rút).
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Yêu cầu R2', 'Dữ liệu kiểm thử.', 700000.00, @far, 'open', 1);
SET @r2 = LAST_INSERT_ID();

INSERT INTO request_bakeries (design_request_id, bakery_id, status, responded_at) VALUES
  (@r2, @bk_a, 'quoted', @now),
  (@r2, @bk_b, 'quoted', @now);

INSERT INTO quotes (design_request_id, bakery_id, revision_no, price, size_description, flavor,
                    decoration_description, estimated_pickup_at, valid_until, created_by_user_id)
VALUES (@r2, @bk_a, 1, 600000.00, '18 cm', 'Vani', '[TEST] Trang trí', @far,
        DATE_ADD(@now, INTERVAL 2 DAY), @owner_a);
SET @q_ok = LAST_INSERT_ID();

-- Báo giá tạo 2 ngày trước, hết hạn 1 giờ trước.
INSERT INTO quotes (design_request_id, bakery_id, revision_no, price, size_description, flavor,
                    decoration_description, estimated_pickup_at, valid_until,
                    created_by_user_id, created_at, updated_at)
VALUES (@r2, @bk_b, 1, 550000.00, '18 cm', 'Vani', '[TEST] Trang trí', @far,
        DATE_SUB(@now, INTERVAL 1 HOUR), @owner_b, DATE_SUB(@now, INTERVAL 2 DAY), DATE_SUB(@now, INTERVAL 2 DAY));
SET @q_exp = LAST_INSERT_ID();

INSERT INTO quotes (design_request_id, bakery_id, revision_no, price, size_description, flavor,
                    decoration_description, estimated_pickup_at, valid_until,
                    created_by_user_id, status, withdrawn_at)
VALUES (@r2, @bk_b, 2, 580000.00, '18 cm', 'Vani', '[TEST] Trang trí', @far,
        DATE_ADD(@now, INTERVAL 2 DAY), @owner_b, 'withdrawn', @now);
SET @q_wd = LAST_INSERT_ID();

-- R3: chỉ gửi cho Tiệm A, có một báo giá còn hạn.
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Yêu cầu R3', 'Dữ liệu kiểm thử.', 500000.00, @far, 'open', 1);
SET @r3 = LAST_INSERT_ID();
INSERT INTO request_bakeries (design_request_id, bakery_id) VALUES (@r3, @bk_a);
INSERT INTO quotes (design_request_id, bakery_id, price, size_description, flavor, decoration_description,
                    estimated_pickup_at, valid_until, created_by_user_id)
VALUES (@r3, @bk_a, 450000.00, '18 cm', 'Vani', '[TEST] Trang trí', @far, DATE_ADD(@now, INTERVAL 2 DAY), @owner_a);
SET @q_r3 = LAST_INSERT_ID();

-- =============================================================================
-- A. Chọn báo giá (giữ từ v1)
-- =============================================================================
CALL t_expect_error('A1. Không chọn được báo giá đã hết hạn',
  'CALL sp_accept_quote(@q_exp, @c1, @o)', 'QUOTE_EXPIRED');
CALL t_expect_error('A2. Không chọn được báo giá đã bị rút lại',
  'CALL sp_accept_quote(@q_wd, @c1, @o)', 'QUOTE_NOT_AVAILABLE');
CALL t_expect_error('A3. Khách khác không chọn được báo giá của yêu cầu không phải của mình',
  'CALL sp_accept_quote(@q_ok, @c2, @o)', 'NOT_REQUEST_OWNER');
CALL t_expect_error('A4. Báo giá không tồn tại',
  'CALL sp_accept_quote(-1, @c1, @o)', 'QUOTE_NOT_FOUND');

SET @chk = (SELECT COUNT(*) = 0 FROM orders WHERE design_request_id = @r2);
CALL t_assert('A5. Các lần chọn thất bại không tạo đơn nào', @chk, 'orders của R2 phải bằng 0');

SET @o = NULL;
CALL t_expect_ok('A6. Chọn báo giá hợp lệ tạo được đơn', 'CALL sp_accept_quote(@q_ok, @c1, @o)');
SET @order_r2 = @o;

SET @chk = (SELECT (SELECT status FROM quotes WHERE id = @q_ok) = 'accepted'
               AND (SELECT status FROM design_requests WHERE id = @r2) = 'ordered'
               AND (SELECT status FROM orders WHERE id = @order_r2) = 'pending_confirmation');
CALL t_assert('A7. Sau khi chọn: báo giá accepted, yêu cầu ordered, đơn pending_confirmation',
  @chk, CONCAT('order_id = ', COALESCE(@order_r2, 'NULL')));

SET @chk = (SELECT (SELECT status FROM quotes WHERE id = @q_exp) = 'not_selected'
               AND (SELECT status FROM quotes WHERE id = @q_wd) = 'withdrawn');
CALL t_assert('A8. Báo giá submitted khác chuyển not_selected; báo giá đã rút giữ nguyên', @chk, NULL);

-- =============================================================================
-- B. Một báo giá không thể tạo nhiều đơn (giữ từ v1)
-- =============================================================================
CALL t_expect_error('B1. Chọn lại cùng báo giá qua thủ tục bị từ chối',
  'CALL sp_accept_quote(@q_ok, @c1, @o)', 'REQUEST_NOT_OPEN');

CALL t_expect_error('B2. INSERT thẳng đơn thứ hai cho cùng báo giá bị UNIQUE chặn',
  'INSERT INTO orders (quote_id, design_request_id, customer_id, bakery_id, agreed_quote_revision_no,
     agreed_price, agreed_pickup_at, agreed_size_description, agreed_flavor, agreed_decoration_description)
   SELECT quote_id, design_request_id, customer_id, bakery_id, agreed_quote_revision_no,
     agreed_price, agreed_pickup_at, agreed_size_description, agreed_flavor, agreed_decoration_description
   FROM orders WHERE id = @order_r2', 'Duplicate entry');

CALL t_expect_error('B3. INSERT thẳng đơn từ báo giá khác của cùng yêu cầu bị UNIQUE chặn',
  'INSERT INTO orders (quote_id, design_request_id, customer_id, bakery_id, agreed_quote_revision_no,
     agreed_price, agreed_pickup_at, agreed_size_description, agreed_flavor, agreed_decoration_description)
   VALUES (@q_exp, @r2, @c1, @bk_b, 1, 550000, @far, ''18 cm'', ''Vani'', ''x'')',
  'uq_orders_design_request');

SET @chk = (SELECT COUNT(*) = 1 FROM orders WHERE quote_id = @q_ok);
CALL t_assert('B4. Báo giá q_ok chỉ có đúng một đơn', @chk, NULL);

-- =============================================================================
-- C. Đơn phải khớp báo giá / yêu cầu / khách (giữ từ v1)
-- =============================================================================
CALL t_expect_error('C1. Đơn ghi sai tiệm so với báo giá bị FK chặn',
  'INSERT INTO orders (quote_id, design_request_id, customer_id, bakery_id, agreed_quote_revision_no,
     agreed_price, agreed_pickup_at, agreed_size_description, agreed_flavor, agreed_decoration_description)
   VALUES (@q_r3, @r3, @c1, @bk_b, 1, 450000, @far, ''18 cm'', ''Vani'', ''x'')', 'fk_orders_quote');

CALL t_expect_error('C2. Đơn ghi sai khách so với yêu cầu bị FK chặn',
  'INSERT INTO orders (quote_id, design_request_id, customer_id, bakery_id, agreed_quote_revision_no,
     agreed_price, agreed_pickup_at, agreed_size_description, agreed_flavor, agreed_decoration_description)
   VALUES (@q_r3, @r3, @c2, @bk_a, 1, 450000, @far, ''18 cm'', ''Vani'', ''x'')', 'fk_orders_request_customer');

-- =============================================================================
-- D. Tiệm phải xác nhận đơn (giữ từ v1)
-- =============================================================================
CALL t_expect_error('D1. Không thể UPDATE thẳng đơn sang confirmed khi chưa có người xác nhận',
  'UPDATE orders SET status = ''confirmed'' WHERE id = @order_r2', 'chk_orders_decision');
CALL t_expect_error('D2. Người xác nhận không thuộc tiệm bị FK chặn (UPDATE thẳng)',
  'UPDATE orders SET status = ''confirmed'', bakery_decided_at = UTC_TIMESTAMP(3),
     bakery_decided_by_user_id = @owner_b WHERE id = @order_r2', 'fk_orders_decided_by_member');
CALL t_expect_error('D3. Nhân viên tiệm khác không xác nhận được qua thủ tục',
  'CALL sp_bakery_decide_order(@order_r2, @owner_b, ''confirm'', NULL)', 'NOT_BAKERY_MEMBER');
CALL t_expect_error('D4. Khách hàng không xác nhận được đơn',
  'CALL sp_bakery_decide_order(@order_r2, @c1, ''confirm'', NULL)', 'NOT_BAKERY_MEMBER');
CALL t_expect_error('D5. Không thể đánh dấu đã nhận bánh khi thiếu picked_up_at',
  'UPDATE orders SET status = ''picked_up'', bakery_decided_at = UTC_TIMESTAMP(3),
     bakery_decided_by_user_id = @owner_a WHERE id = @order_r2', 'chk_orders_picked_up');
CALL t_expect_ok('D6. Chủ Tiệm A xác nhận đơn của tiệm mình',
  'CALL sp_bakery_decide_order(@order_r2, @owner_a, ''confirm'', NULL)');

SET @chk = (SELECT status = 'confirmed' AND bakery_decided_at IS NOT NULL AND bakery_decided_by_user_id = @owner_a
            FROM orders WHERE id = @order_r2);
CALL t_assert('D7. Đơn đã confirmed, có thời điểm và người xác nhận', @chk, NULL);

CALL t_expect_error('D8. Không xác nhận lại đơn đã xử lý',
  'CALL sp_bakery_decide_order(@order_r2, @owner_a, ''reject'', ''x'')', 'ORDER_NOT_PENDING');

-- =============================================================================
-- E. Ràng buộc schema của báo giá, yêu cầu, ảnh (giữ từ v1)
-- =============================================================================
CALL t_expect_error('E1. Tiệm không nhận yêu cầu thì không báo giá được (INSERT thẳng)',
  'INSERT INTO quotes (design_request_id, bakery_id, price, size_description, flavor, decoration_description,
     estimated_pickup_at, valid_until, created_by_user_id)
   VALUES (@r3, @bk_b, 400000, ''18 cm'', ''Vani'', ''x'', @far, DATE_ADD(UTC_TIMESTAMP(), INTERVAL 2 DAY), @owner_b)',
  'fk_quotes_request_bakery');
CALL t_expect_error('E2. Người không thuộc tiệm không tạo báo giá được (INSERT thẳng)',
  'INSERT INTO quotes (design_request_id, bakery_id, revision_no, price, size_description, flavor, decoration_description,
     estimated_pickup_at, valid_until, created_by_user_id, status, withdrawn_at)
   VALUES (@r3, @bk_a, 2, 400000, ''18 cm'', ''Vani'', ''x'', @far, DATE_ADD(UTC_TIMESTAMP(), INTERVAL 2 DAY),
     @owner_b, ''withdrawn'', UTC_TIMESTAMP(3))',
  'fk_quotes_created_by_member');
CALL t_expect_error('E3. Giá báo âm bị CHECK chặn',
  'INSERT INTO quotes (design_request_id, bakery_id, revision_no, price, size_description, flavor, decoration_description,
     estimated_pickup_at, valid_until, created_by_user_id, status, withdrawn_at)
   VALUES (@r3, @bk_a, 3, -1, ''18 cm'', ''Vani'', ''x'', @far, DATE_ADD(UTC_TIMESTAMP(), INTERVAL 2 DAY),
     @owner_a, ''withdrawn'', UTC_TIMESTAMP(3))',
  'chk_quotes_price');
CALL t_expect_error('E4. Đánh dấu withdrawn mà thiếu withdrawn_at bị CHECK chặn',
  'UPDATE quotes SET status = ''withdrawn'' WHERE id = @q_r3', 'chk_quotes_withdrawn');
CALL t_expect_error('E5. Ngân sách tối thiểu lớn hơn tối đa bị CHECK chặn',
  'INSERT INTO design_requests (customer_id, title, idea_description, budget_min, budget_max, pickup_at)
   VALUES (@c1, ''[TEST] sai ngân sách'', ''x'', 900000, 500000, @far)', 'chk_design_requests_budget');
CALL t_expect_error('E6. Gửi cùng yêu cầu cho cùng tiệm hai lần bị UNIQUE chặn (INSERT thẳng)',
  'INSERT INTO request_bakeries (design_request_id, bakery_id) VALUES (@r3, @bk_a)',
  'uq_request_bakeries_request_bakery');
CALL t_expect_error('E7. Ảnh tham khảo của khách không được gắn chủ sở hữu là tiệm',
  'INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id)
   VALUES (''customer_reference'', ''https://example.com/x.jpg'', ''image/jpeg'', @c1, @bk_a, @r3)',
  'chk_media_assets_owner_by_kind');
CALL t_expect_error('E8. Ảnh đề xuất của tiệm chưa nhận yêu cầu bị FK chặn',
  'INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id)
   VALUES (''design_proposal'', ''https://example.com/x.jpg'', ''image/jpeg'', @owner_b, @bk_b, @r3)',
  'fk_media_assets_request_bakery');
CALL t_expect_error('E9. Trạng thái không hợp lệ bị CHECK chặn',
  'UPDATE design_requests SET status = ''done'' WHERE id = @r3', 'chk_design_requests_status');
CALL t_expect_error('E10. Báo giá thiếu cả kích thước và số phần ăn bị CHECK chặn',
  'UPDATE quotes SET size_description = NULL, servings = NULL WHERE id = @q_r3', 'chk_quotes_size');

-- =============================================================================
-- J. Gửi yêu cầu cho tiệm (v2)
-- =============================================================================
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Yêu cầu RJ (draft)', 'Dữ liệu kiểm thử.', 500000.00, @far, 'draft', 1);
SET @rj = LAST_INSERT_ID();

CALL t_expect_ok('J1. Khách gửi yêu cầu draft cho tiệm active', 'CALL sp_send_request_to_bakery(@rj, @c1, @bk_a)');
SET @chk = (SELECT status = 'open' FROM design_requests WHERE id = @rj);
CALL t_assert('J2. Yêu cầu draft chuyển open sau khi gửi', @chk, NULL);
CALL t_expect_error('J3. Gửi lại cho cùng tiệm bị từ chối',
  'CALL sp_send_request_to_bakery(@rj, @c1, @bk_a)', 'REQUEST_ALREADY_SENT');
CALL t_expect_error('J4. Khách khác không gửi được yêu cầu không phải của mình',
  'CALL sp_send_request_to_bakery(@rj, @c2, @bk_b)', 'NOT_REQUEST_OWNER');
CALL t_expect_error('J5. Yêu cầu không tồn tại',
  'CALL sp_send_request_to_bakery(-1, @c1, @bk_b)', 'REQUEST_NOT_FOUND');
CALL t_expect_error('J6. Không gửi được cho tiệm draft (chưa active)',
  'CALL sp_send_request_to_bakery(@rj, @c1, @bk_d)', 'BAKERY_NOT_ACTIVE');
CALL t_expect_error('J7. Không gửi được yêu cầu đã đóng',
  'CALL sp_send_request_to_bakery(@r2, @c1, @bk_c)', 'REQUEST_NOT_OPEN');

-- Yêu cầu có ngày nhận đã qua (tạo lùi thời gian để thỏa CHECK pickup_at > created_at).
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo,
                             created_at, updated_at)
VALUES (@c1, '[TEST] Yêu cầu RJ quá hạn nhận', 'x', 500000.00, DATE_SUB(@now, INTERVAL 1 DAY), 'draft', 1,
        DATE_SUB(@now, INTERVAL 3 DAY), DATE_SUB(@now, INTERVAL 3 DAY));
SET @rj_past = LAST_INSERT_ID();
CALL t_expect_error('J8. Không gửi được yêu cầu có ngày nhận đã qua',
  'CALL sp_send_request_to_bakery(@rj_past, @c1, @bk_a)', 'REQUEST_PICKUP_IN_PAST');

-- Tiệm đã từ chối yêu cầu thì không báo giá được.
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Yêu cầu RJ2', 'x', 500000.00, @far, 'draft', 1);
SET @rj2 = LAST_INSERT_ID();
CALL sp_send_request_to_bakery(@rj2, @c1, @bk_b);
UPDATE request_bakeries SET status = 'declined', responded_at = UTC_TIMESTAMP(3), decline_reason = '[TEST]'
 WHERE design_request_id = @rj2 AND bakery_id = @bk_b;
CALL t_expect_error('J9. Tiệm đã từ chối yêu cầu thì không tạo được báo giá',
  'CALL t_quote(@rj2, @bk_b, @owner_b, NULL, 86400)', 'REQUEST_DECLINED_BY_BAKERY');

-- =============================================================================
-- H. Phiên bản báo giá (v2)
-- =============================================================================
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Yêu cầu RH', 'Dữ liệu kiểm thử.', 900000.00, @far, 'draft', 1);
SET @rh = LAST_INSERT_ID();
CALL sp_send_request_to_bakery(@rh, @c1, @bk_a);
CALL sp_send_request_to_bakery(@rh, @c1, @bk_b);

CALL t_expect_ok('H0. Tiệm A tạo báo giá qua thủ tục', 'CALL t_quote(@rh, @bk_a, @owner_a, NULL, 86400)');
SET @q_h1 = @last_quote;
CALL t_expect_ok('H0b. Tiệm B tạo báo giá qua thủ tục', 'CALL t_quote(@rh, @bk_b, @owner_b, NULL, 86400)');
SET @q_hb = @last_quote;

SET @chk = (SELECT status = 'quoted' AND responded_at IS NOT NULL FROM request_bakeries
            WHERE design_request_id = @rh AND bakery_id = @bk_a);
CALL t_assert('H0c. request_bakeries chuyển quoted khi tiệm báo giá', @chk, NULL);

CALL t_expect_error('H3. Tạo báo giá thứ hai khi đã có phiên bản hiện hành bị từ chối',
  'CALL t_quote(@rh, @bk_a, @owner_a, NULL, 86400)', 'QUOTE_ALREADY_EXISTS');

CALL t_expect_ok('H1. Sửa báo giá tạo phiên bản mới', 'CALL t_revise(@q_h1, @owner_a, NULL, 520000, 86400)');
SET @q_h2 = @last_quote;
SET @chk = (SELECT (SELECT status FROM quotes WHERE id = @q_h1) = 'superseded'
               AND (SELECT status = 'submitted' AND revision_no = 2 AND price = 520000 AND flavor = 'Sô-cô-la'
                    FROM quotes WHERE id = @q_h2)
               AND (SELECT COUNT(*) FROM quotes
                    WHERE design_request_id = @rh AND bakery_id = @bk_a AND current_slot = 1) = 1);
CALL t_assert('H1b. Bản cũ superseded, bản mới rev 2 submitted, chỉ một phiên bản hiện hành', @chk,
  CONCAT('q_h1 = ', COALESCE(@q_h1, 'NULL'), ', q_h2 = ', COALESCE(@q_h2, 'NULL')));

CALL t_expect_error('H2. Sửa lại phiên bản đã superseded bị từ chối',
  'CALL t_revise(@q_h1, @owner_a, NULL, 530000, 86400)', 'QUOTE_NOT_CURRENT');

CALL t_expect_error('H4. INSERT thẳng phiên bản submitted thứ hai bị UNIQUE uq_quotes_current chặn',
  'INSERT INTO quotes (design_request_id, bakery_id, revision_no, price, size_description, flavor,
     decoration_description, estimated_pickup_at, valid_until, created_by_user_id)
   VALUES (@rh, @bk_a, 9, 400000, ''18 cm'', ''Vani'', ''x'', @far, DATE_ADD(UTC_TIMESTAMP(), INTERVAL 2 DAY), @owner_a)',
  'uq_quotes_current');

CALL t_expect_error('H8. Sửa báo giá với hạn trong quá khứ bị từ chối',
  'CALL t_revise(@q_h2, @owner_a, NULL, 520000, -10)', 'QUOTE_INVALID_TIMES');
CALL t_expect_error('H9. Sửa báo giá với giá 0 bị từ chối',
  'CALL t_revise(@q_h2, @owner_a, NULL, 0, 86400)', 'QUOTE_INVALID_PRICE');
CALL t_expect_error('H9b. Báo giá thiếu hương vị bị từ chối',
  'CALL sp_create_quote(@rj, @bk_a, @owner_a, 1000, ''18 cm'', NULL, NULL, ''x'', NULL, NULL, @far,
     DATE_ADD(UTC_TIMESTAMP(), INTERVAL 1 DAY), NULL, @o)', 'INVALID_ARGUMENT');
CALL t_expect_error('H10. Tiệm chưa nhận yêu cầu không tạo được báo giá',
  'CALL t_quote(@rh, @bk_c, @owner_c, NULL, 86400)', 'REQUEST_NOT_SENT_TO_BAKERY');

CALL t_expect_ok('H7. Tiệm rút phiên bản hiện hành', 'CALL sp_withdraw_quote(@q_h2, @owner_a)');
CALL t_expect_ok('H7b. Sau khi rút, tiệm tạo được báo giá mới', 'CALL t_quote(@rh, @bk_a, @owner_a, NULL, 86400)');
SET @q_h3 = @last_quote;
SET @chk = (SELECT revision_no = 3 AND status = 'submitted' FROM quotes WHERE id = @q_h3);
CALL t_assert('H7c. Báo giá mới sau khi rút có revision_no tiếp theo (3)', @chk, NULL);

CALL t_expect_ok('H5a. Khách chọn phiên bản 3 của Tiệm A', 'CALL sp_accept_quote(@q_h3, @c1, @o)');
SET @order_h = @o;
CALL t_expect_error('H5. Không sửa được phiên bản đã được khách chọn',
  'CALL t_revise(@q_h3, @owner_a, NULL, 1, 86400)', 'QUOTE_ALREADY_ACCEPTED');
CALL t_expect_error('H5b. Không rút được phiên bản đã được khách chọn',
  'CALL sp_withdraw_quote(@q_h3, @owner_a)', 'QUOTE_ALREADY_ACCEPTED');
CALL t_expect_error('H6. Không sửa báo giá khi yêu cầu đã đóng',
  'CALL t_revise(@q_hb, @owner_b, NULL, 1000, 86400)', 'REQUEST_NOT_OPEN');
CALL t_expect_error('H6b. Không tạo báo giá khi yêu cầu đã đóng',
  'CALL t_quote(@rh, @bk_b, @owner_b, NULL, 86400)', 'REQUEST_NOT_OPEN');

-- =============================================================================
-- F. Ảnh thiết kế của báo giá (v2)
-- =============================================================================
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] Yêu cầu RF', 'Dữ liệu kiểm thử.', 900000.00, @far, 'draft', 1);
SET @rf = LAST_INSERT_ID();
CALL sp_send_request_to_bakery(@rf, @c1, @bk_a);
CALL sp_send_request_to_bakery(@rf, @c1, @bk_b);

INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, is_demo)
VALUES ('design_proposal', 'https://example.com/test/rf-a-v1.jpg', 'image/jpeg', @owner_a, @bk_a, @rf, 1, 1);
SET @pa = LAST_INSERT_ID();
INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, is_demo)
VALUES ('design_proposal', 'https://example.com/test/rf-b-v1.jpg', 'image/jpeg', @owner_b, @bk_b, @rf, 1, 1);
SET @pb = LAST_INSERT_ID();
INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, design_request_id, is_demo)
VALUES ('customer_reference', 'https://example.com/test/rf-khach.jpg', 'image/jpeg', @c1, @rf, 1);
SET @cref = LAST_INSERT_ID();
SELECT id INTO @cake_photo FROM media_assets WHERE asset_kind = 'cake_photo' AND owner_bakery_id = @bk_b LIMIT 1;
INSERT INTO media_assets (asset_kind, storage_url, mime_type, uploaded_by_user_id, owner_bakery_id, design_request_id, version_no, is_demo)
VALUES ('design_proposal', 'https://example.com/test/r3-a.jpg', 'image/jpeg', @owner_a, @bk_a, @r3, 1, 1);
SET @pa_other_request = LAST_INSERT_ID();

CALL t_expect_error('F1. Tiệm A không dùng được ảnh đề xuất của Tiệm B khi tạo báo giá',
  'CALL t_quote(@rf, @bk_a, @owner_a, @pb, 86400)', 'DESIGN_ASSET_WRONG_BAKERY');
CALL t_expect_ok('F3. Tiệm A dùng ảnh tham khảo của khách làm thiết kế xác nhận',
  'CALL t_quote(@rf, @bk_a, @owner_a, @cref, 86400)');
SET @q_fa = @last_quote;
CALL t_expect_error('F2. Tiệm A không dùng được ảnh của Tiệm B khi sửa báo giá',
  'CALL t_revise(@q_fa, @owner_a, @pb, 600000, 86400)', 'DESIGN_ASSET_WRONG_BAKERY');
CALL t_expect_error('F4. Không dùng ảnh mẫu bánh (cake_photo) làm thiết kế của báo giá',
  'CALL t_quote(@rf, @bk_b, @owner_b, @cake_photo, 86400)', 'DESIGN_ASSET_WRONG_REQUEST');
CALL t_expect_error('F5. Không dùng ảnh của yêu cầu khác',
  'CALL t_quote(@rf, @bk_b, @owner_b, @pa_other_request, 86400)', 'DESIGN_ASSET_WRONG_REQUEST');
CALL t_expect_error('F6. Ảnh không tồn tại',
  'CALL t_quote(@rf, @bk_b, @owner_b, -1, 86400)', 'DESIGN_ASSET_NOT_FOUND');
CALL t_expect_ok('F8. Tiệm B dùng ảnh đề xuất của chính mình',
  'CALL t_quote(@rf, @bk_b, @owner_b, @pb, 86400)');
SET @q_fb = @last_quote;
CALL t_expect_ok('F9. Tiệm A sửa báo giá dùng ảnh đề xuất của chính mình',
  'CALL t_revise(@q_fa, @owner_a, @pa, 600000, 86400)');
SET @q_fa2 = @last_quote;
-- Mô phỏng dữ liệu ảnh bị sửa trực tiếp sau khi báo giá đã tạo (bỏ qua thủ tục):
-- ảnh đề xuất của A bị đổi chủ sở hữu sang B. Khách chọn báo giá A phải bị chặn.
UPDATE media_assets SET owner_bakery_id = @bk_b WHERE id = @pa;
CALL t_expect_error('F7. Kiểm tra lại ảnh trước khi khách chọn: ảnh không còn thuộc tiệm báo giá',
  'CALL sp_accept_quote(@q_fa2, @c1, @o)', 'DESIGN_ASSET_WRONG_BAKERY');
SET @chk = (SELECT COUNT(*) = 0 FROM orders WHERE design_request_id = @rf);
CALL t_assert('F7b. Không có đơn nào được tạo cho RF', @chk, NULL);
UPDATE media_assets SET owner_bakery_id = @bk_a WHERE id = @pa;

-- =============================================================================
-- I. Bản chốt của đơn không đổi khi dữ liệu nguồn bị sửa (v2)
-- =============================================================================
CALL t_expect_ok('I1. Khách chọn báo giá Tiệm B có ảnh đề xuất', 'CALL sp_accept_quote(@q_fb, @c1, @o)');
SET @order_i = @o;
SET @snap_before = (
  SELECT CONCAT_WS('|', agreed_quote_revision_no, agreed_price, agreed_pickup_at,
                   COALESCE(agreed_size_description, '-'), COALESCE(agreed_servings, '-'),
                   agreed_flavor, agreed_decoration_description, COALESCE(agreed_inscription_text, '-'),
                   COALESCE(agreed_note, '-'), agreed_design_asset_id, agreed_design_asset_kind,
                   COALESCE(agreed_design_version_no, '-'), agreed_design_url)
  FROM orders WHERE id = @order_i);
SET @chk = (SELECT agreed_flavor = 'Vani' AND agreed_design_asset_id = @pb
               AND agreed_design_url = 'https://example.com/test/rf-b-v1.jpg' AND agreed_design_version_no = 1
            FROM orders WHERE id = @order_i);
CALL t_assert('I2. Đơn sao chép nội dung phiên bản báo giá và ảnh thiết kế', @chk, @snap_before);

-- Sửa trực tiếp mọi dữ liệu nguồn (giả lập lỗi backend / admin sửa tay).
UPDATE design_requests
   SET size_description = 'ĐÃ SỬA', flavor = 'ĐÃ SỬA', inscription_text = 'ĐÃ SỬA', idea_description = 'ĐÃ SỬA'
 WHERE id = @rf;
UPDATE media_assets SET storage_url = 'https://example.com/test/da-sua.jpg', version_no = 99 WHERE id = @pb;
UPDATE quotes
   SET price = 1, flavor = 'ĐÃ SỬA', decoration_description = 'ĐÃ SỬA', inscription_text = 'ĐÃ SỬA',
       note = 'ĐÃ SỬA', size_description = 'ĐÃ SỬA', estimated_pickup_at = DATE_ADD(@far, INTERVAL 5 DAY)
 WHERE id = @q_fb;

SET @snap_after = (
  SELECT CONCAT_WS('|', agreed_quote_revision_no, agreed_price, agreed_pickup_at,
                   COALESCE(agreed_size_description, '-'), COALESCE(agreed_servings, '-'),
                   agreed_flavor, agreed_decoration_description, COALESCE(agreed_inscription_text, '-'),
                   COALESCE(agreed_note, '-'), agreed_design_asset_id, agreed_design_asset_kind,
                   COALESCE(agreed_design_version_no, '-'), agreed_design_url)
  FROM orders WHERE id = @order_i);
CALL t_assert('I3. Sửa yêu cầu, ảnh và báo giá không làm đổi bản chốt của đơn',
  @snap_before = @snap_after, @snap_after);

-- =============================================================================
-- G. Quyền và trạng thái hoạt động (v2)
-- =============================================================================
-- Chuẩn bị: yêu cầu gửi cho Tiệm C trước khi C bị suspended.
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] RG5', 'x', 500000, @far, 'draft', 1);
SET @rg5 = LAST_INSERT_ID();
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] RG7', 'x', 500000, @far, 'draft', 1);
SET @rg7 = LAST_INSERT_ID();
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] RG8', 'x', 500000, @far, 'draft', 1);
SET @rg8 = LAST_INSERT_ID();
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] RG3', 'x', 500000, @far, 'draft', 1);
SET @rg3 = LAST_INSERT_ID();
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c3, '[TEST] RG1 của khách sẽ bị khóa', 'x', 500000, @far, 'draft', 1);
SET @rg1 = LAST_INSERT_ID();

CALL sp_send_request_to_bakery(@rg5, @c1, @bk_c);
CALL sp_send_request_to_bakery(@rg7, @c1, @bk_c);
CALL sp_send_request_to_bakery(@rg8, @c1, @bk_c);
CALL sp_send_request_to_bakery(@rg3, @c1, @bk_a);
CALL sp_send_request_to_bakery(@rg1, @c3, @bk_a);

CALL t_quote(@rg7, @bk_c, @owner_c, NULL, 86400);  SET @q_g7 = @last_quote;
CALL t_quote(@rg8, @bk_c, @owner_c, NULL, 86400);  SET @q_g8 = @last_quote;
CALL sp_accept_quote(@q_g8, @c1, @order_g8);
CALL t_quote(@rg1, @bk_a, @owner_a, NULL, 86400);  SET @q_g1 = @last_quote;

CALL t_expect_ok('G0. Nhân viên A2 (đang active) tạo được báo giá',
  'CALL t_quote(@rg3, @bk_a, @staff_a2, NULL, 86400)');
SET @q_g3 = @last_quote;
CALL sp_accept_quote(@q_g3, @c1, @order_g3);

-- Khóa tài khoản / gỡ thành viên / đình chỉ tiệm
UPDATE users SET status = 'disabled' WHERE id = @c3;
UPDATE bakery_members SET status = 'removed' WHERE bakery_id = @bk_a AND user_id = @staff_a2;
UPDATE bakeries SET profile_status = 'suspended' WHERE id = @bk_c;

CALL t_expect_error('G1. Khách bị disabled không chọn được báo giá',
  'CALL sp_accept_quote(@q_g1, @c3, @o)', 'USER_NOT_ACTIVE');
CALL t_expect_error('G1b. Khách bị disabled không gửi được yêu cầu',
  'CALL sp_send_request_to_bakery(@rg1, @c3, @bk_b)', 'USER_NOT_ACTIVE');
CALL t_expect_error('G2. Thành viên removed không tạo được báo giá',
  'CALL t_quote(@rj, @bk_a, @staff_a2, NULL, 86400)', 'NOT_BAKERY_MEMBER');
CALL t_expect_error('G2b. Thành viên removed không sửa được báo giá',
  'CALL t_revise(@q_g1, @staff_a2, NULL, 1000, 86400)', 'NOT_BAKERY_MEMBER');
CALL t_expect_error('G3. Thành viên removed không quyết định được đơn',
  'CALL sp_bakery_decide_order(@order_g3, @staff_a2, ''confirm'', NULL)', 'NOT_BAKERY_MEMBER');
CALL t_expect_error('G4. Tài khoản nhân viên bị disabled không tạo được báo giá',
  'CALL t_quote(@rj, @bk_a, @staff_dis, NULL, 86400)', 'USER_NOT_ACTIVE');
CALL t_expect_error('G5. Tiệm suspended không tạo được báo giá',
  'CALL t_quote(@rg5, @bk_c, @owner_c, NULL, 86400)', 'BAKERY_NOT_ACTIVE');
CALL t_expect_error('G6. Không gửi được yêu cầu cho tiệm suspended',
  'CALL sp_send_request_to_bakery(@rj, @c1, @bk_c)', 'BAKERY_NOT_ACTIVE');
CALL t_expect_error('G7. Khách không chọn được báo giá của tiệm suspended',
  'CALL sp_accept_quote(@q_g7, @c1, @o)', 'BAKERY_NOT_ACTIVE');
CALL t_expect_error('G8. Tiệm suspended không xác nhận được đơn',
  'CALL sp_bakery_decide_order(@order_g8, @owner_c, ''confirm'', NULL)', 'BAKERY_NOT_ACTIVE');
CALL t_expect_ok('G8b. Tiệm suspended vẫn từ chối được đơn đang chờ',
  'CALL sp_bakery_decide_order(@order_g8, @owner_c, ''reject'', ''[TEST] Tiệm tạm ngừng'')');
SET @chk = (SELECT (SELECT status FROM orders WHERE id = @order_g8) = 'rejected_by_bakery'
               AND (SELECT status FROM design_requests WHERE id = @rg8) = 'cancelled');
CALL t_assert('G8c. Từ chối: đơn rejected_by_bakery, yêu cầu cancelled (giả định v1)', @chk, NULL);
CALL t_expect_error('G9. Tài khoản tiệm không chọn báo giá thay khách',
  'CALL sp_accept_quote(@q_h3, @owner_a, @o)', 'USER_NOT_CUSTOMER');

-- Tham số NULL / ID không tồn tại
CALL t_expect_error('G10. accept: quote_id NULL', 'CALL sp_accept_quote(NULL, @c1, @o)', 'INVALID_ARGUMENT');
CALL t_expect_error('G11. accept: customer_id NULL', 'CALL sp_accept_quote(@q_g1, NULL, @o)', 'INVALID_ARGUMENT');
CALL t_expect_error('G12. accept: user không tồn tại', 'CALL sp_accept_quote(@q_ok, -1, @o)', 'USER_NOT_FOUND');
CALL t_expect_error('G13. decide: actor NULL',
  'CALL sp_bakery_decide_order(@order_g3, NULL, ''confirm'', NULL)', 'INVALID_ARGUMENT');
CALL t_expect_error('G14. decide: quyết định không hợp lệ',
  'CALL sp_bakery_decide_order(@order_g3, @owner_a, ''maybe'', NULL)', 'INVALID_DECISION');
CALL t_expect_error('G15. decide: đơn không tồn tại',
  'CALL sp_bakery_decide_order(-1, @owner_a, ''confirm'', NULL)', 'ORDER_NOT_FOUND');
CALL t_expect_error('G16. create: request_id NULL',
  'CALL t_quote(NULL, @bk_a, @owner_a, NULL, 86400)', 'INVALID_ARGUMENT');
CALL t_expect_error('G17. create: yêu cầu không tồn tại',
  'CALL t_quote(-1, @bk_a, @owner_a, NULL, 86400)', 'REQUEST_NOT_FOUND');
CALL t_expect_error('G18. revise: báo giá không tồn tại',
  'CALL t_revise(-1, @owner_a, NULL, 1000, 86400)', 'QUOTE_NOT_FOUND');
CALL t_expect_error('G19. send: bakery_id NULL',
  'CALL sp_send_request_to_bakery(@rj, @c1, NULL)', 'INVALID_ARGUMENT');
CALL t_expect_error('G20. send: tiệm không tồn tại',
  'CALL sp_send_request_to_bakery(@rj, @c1, -1)', 'BAKERY_NOT_FOUND');
CALL t_expect_ok('G21. Chủ Tiệm A (active) vẫn xác nhận được đơn do nhân viên đã nghỉ báo giá',
  'CALL sp_bakery_decide_order(@order_g3, @owner_a, ''confirm'', NULL)');

-- =============================================================================
-- K. Kiểm tra hạn theo thời gian thực (v2)
-- =============================================================================
INSERT INTO design_requests (customer_id, title, idea_description, budget_max, pickup_at, status, is_demo)
VALUES (@c1, '[TEST] RK', 'x', 500000, @far, 'draft', 1);
SET @rk = LAST_INSERT_ID();
CALL sp_send_request_to_bakery(@rk, @c1, @bk_a);
CALL t_quote(@rk, @bk_a, @owner_a, NULL, 2);   -- hết hạn sau 2 giây
SET @q_k = @last_quote;

-- Một câu lệnh CALL: ngủ 3 giây rồi mới chọn. Báo giá đã hết hạn trước khi kiểm tra.
CALL t_expect_error('K1. Báo giá hết hạn trong lúc một câu lệnh đang chạy vẫn bị từ chối',
  'CALL t_sleep_then_accept(@q_k, @c1, 3)', 'QUOTE_EXPIRED');
SET @chk = (SELECT COUNT(*) = 0 FROM orders WHERE design_request_id = @rk);
CALL t_assert('K2. Không có đơn nào được tạo cho RK', @chk, NULL);
CALL t_assert('INFO. NOW() trong thủ tục lồng nhau trên server này',
  1, CONCAT('NOW() trước SLEEP = ',
            COALESCE(CAST(@t_now_before AS CHAR CHARACTER SET utf8mb4) COLLATE utf8mb4_0900_ai_ci, 'NULL'),
            ', sau SLEEP = ',
            COALESCE(CAST(@t_now_after AS CHAR CHARACTER SET utf8mb4) COLLATE utf8mb4_0900_ai_ci, 'NULL'),
            IF(@t_now_before = @t_now_after, ' => bị cố định trong câu lệnh ngoài', ' => thay đổi theo từng câu lệnh')));

-- =============================================================================
-- Kết quả
-- =============================================================================
SELECT seq, IF(passed, 'PASS', 'FAIL') AS result, test_name, detail
FROM test_results
ORDER BY seq;

SELECT SUM(passed) AS passed, SUM(1 - passed) AS failed, COUNT(*) AS total
FROM test_results;

DROP PROCEDURE IF EXISTS t_expect_error;
DROP PROCEDURE IF EXISTS t_expect_ok;
DROP PROCEDURE IF EXISTS t_assert;
DROP PROCEDURE IF EXISTS t_quote;
DROP PROCEDURE IF EXISTS t_revise;
DROP PROCEDURE IF EXISTS t_sleep_then_accept;
