# Stored procedure của `decakes_db`

Nguồn: `install/02_procedures.sql` (viết tắt **P**, kèm số dòng). Tài liệu lập từ mã nguồn SQL, chưa chạy trên database nào. Chỗ chưa rõ hoặc chưa xác minh được từ SQL đánh dấu **[cần xác minh]**.

## Số lượng thực tế

Đếm `CREATE PROCEDURE` trong P: **11** (khớp kỳ vọng), gồm 5 procedure kiểm tra (`sp_guard_*`) và 6 procedure nghiệp vụ. Không có function hay trigger. Script chạy `DROP PROCEDURE IF EXISTS` cho cả 11 rồi tạo lại, nên chạy lại được (xem [README.md](README.md) về cảnh báo vận hành).

## Tóm tắt

| # | Procedure | Loại | Tự mở transaction? | Gọi trên connection không có transaction ứng dụng? | Tham số OUT |
| --- | --- | --- | --- | --- | --- |
| 1 | `sp_guard_active_customer` | guard | Không | Không áp dụng (không gọi trực tiếp) | không |
| 2 | `sp_guard_active_bakery_member` | guard | Không | Không áp dụng | không |
| 3 | `sp_guard_bakery_active` | guard | Không | Không áp dụng | không |
| 4 | `sp_guard_quote_design` | guard | Không | Không áp dụng | không |
| 5 | `sp_guard_quote_content` | guard | Không | Không áp dụng | không |
| 6 | `sp_send_request_to_bakery` | nghiệp vụ | **Có** | **Bắt buộc** | không |
| 7 | `sp_create_quote` | nghiệp vụ | **Có** | **Bắt buộc** | `p_quote_id` |
| 8 | `sp_revise_quote` | nghiệp vụ | **Có** | **Bắt buộc** | `p_new_quote_id` |
| 9 | `sp_withdraw_quote` | nghiệp vụ | **Có** | **Bắt buộc** | không |
| 10 | `sp_accept_quote` | nghiệp vụ | **Có** | **Bắt buộc** | `p_order_id` |
| 11 | `sp_bakery_decide_order` | nghiệp vụ | **Có** | **Bắt buộc** | không |

Nhóm tự quản lý transaction: 6 procedure nghiệp vụ (số 6-11). Lý do bắt buộc gọi trên connection không có transaction của ứng dụng đang mở: lệnh `START TRANSACTION` trong procedure sẽ ngầm `COMMIT` transaction đang mở (ghi chú P dòng 22-24). Nhóm guard không mở transaction và chỉ được gọi từ bên trong procedure nghiệp vụ, backend không nên gọi trực tiếp.

## Quy ước chung của mọi procedure nghiệp vụ

Từ P dòng 11-51 và cấu trúc từng procedure:

- **Xác thực khác phân quyền.** `p_actor_user_id` / `p_customer_id` do backend lấy từ phiên đăng nhập đã xác thực. Procedure chỉ kiểm tra quyền (trạng thái, vai trò, thành viên tiệm, chủ yêu cầu), không xác thực.
- **Lỗi nghiệp vụ**: `SIGNAL SQLSTATE '45000'` với `MESSAGE_TEXT` là mã ngắn (ví dụ `QUOTE_EXPIRED`). Không đặt `MYSQL_ERRNO` nên số lỗi MySQL là mặc định của `SIGNAL` (1644) **[cần xác minh trên MySqlConnector]**.
- **Transaction và rollback.** Cả 6 procedure nghiệp vụ có `DECLARE EXIT HANDLER FOR SQLEXCEPTION BEGIN ROLLBACK; RESIGNAL; END`. Mọi lỗi, kể cả lỗi hệ thống MySQL như vi phạm CHECK hoặc UNIQUE, deadlock, lock wait timeout, đều rollback toàn bộ rồi ném lại nguyên lỗi gốc. Các lỗi không phải nghiệp vụ này **không** mang SQLSTATE 45000.
- **Thứ tự khóa thống nhất** để tránh deadlock: `design_requests → request_bakeries → quotes → orders`, sau đó khóa chia sẻ (`FOR SHARE`) `users, bakeries, bakery_members, media_assets` (qua guard).
- **Thời gian hiện tại** `v_now` lấy sau khi giữ đủ khóa: `SYSDATE(3) - INTERVAL TIMESTAMPDIFF(MICROSECOND, UTC_TIMESTAMP(3), NOW(3)) MICROSECOND`, tức giờ UTC thực, không phụ thuộc `time_zone` của session. Yêu cầu: gọi trực tiếp bằng `CALL`, không từ trigger hay function (P dòng 33-50).
- **Không procedure nào trả result set.** Kết quả trả về chỉ qua `OUT` parameter (3 procedure) hoặc không trả gì (3 procedure). Backend phải truy vấn lại bằng SELECT để lấy dữ liệu sau khi gọi.
- Trong 4 procedure có `START TRANSACTION` sau một lần tra cứu id (revise, withdraw, accept, decide), lần tra cứu đầu tiên chạy **ngoài transaction và không khóa**, chỉ để lấy cột không bao giờ đổi (`design_request_id`, `bakery_id`).

---

## 1. `sp_guard_active_customer`

- **Vị trí**: P dòng 76-96.
- **Chức năng**: kiểm tra user tồn tại, `active`, và `system_role = 'customer'`.
- **Tham số**: `IN p_user_id BIGINT`.
- **Trả về**: không (chỉ ném lỗi).
- **Đọc**: `users` với `FOR SHARE`. **Ghi**: không.
- **Lỗi** (theo thứ tự): `INVALID_ARGUMENT` (NULL), `USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `USER_NOT_CUSTOMER`.
- **Transaction**: không tự mở, không có handler; lỗi lan ra procedure gọi để rollback.
- **Khóa**: khóa chia sẻ dòng `users`.
- **Lưu ý backend**: không gọi trực tiếp. Gọi từ `sp_send_request_to_bakery`, `sp_accept_quote`.

## 2. `sp_guard_active_bakery_member`

- **Vị trí**: P dòng 98-123.
- **Chức năng**: user tồn tại, `active`, `system_role = 'bakery'` và là thành viên `active` của tiệm.
- **Tham số**: `IN p_bakery_id BIGINT`, `IN p_user_id BIGINT`.
- **Trả về**: không.
- **Đọc**: `users`, `bakery_members` (đều `FOR SHARE`). **Ghi**: không.
- **Lỗi**: `INVALID_ARGUMENT`, `USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `NOT_BAKERY_MEMBER` (sai vai trò, không phải thành viên, hoặc thành viên `removed`: cả ba trường hợp dùng chung một mã).
- **Transaction/khóa**: như guard số 1.
- **Lưu ý**: không phân biệt `owner` và `staff`; mọi thành viên `active` đều có cùng quyền trong các procedure. **[cần chốt nghiệp vụ nếu muốn phân quyền chủ/nhân viên]**.

## 3. `sp_guard_bakery_active`

- **Vị trí**: P dòng 125-141.
- **Chức năng**: tiệm tồn tại và `profile_status = 'active'`.
- **Tham số**: `IN p_bakery_id BIGINT`.
- **Đọc**: `bakeries` (`FOR SHARE`). **Ghi**: không.
- **Lỗi**: `INVALID_ARGUMENT`, `BAKERY_NOT_FOUND`, `BAKERY_NOT_ACTIVE`.
- **Transaction/khóa**: như guard số 1.

## 4. `sp_guard_quote_design`

- **Vị trí**: P dòng 143-175.
- **Chức năng**: kiểm tra ảnh thiết kế đính kèm báo giá. `p_asset_id` NULL được chấp nhận (không kèm ảnh, thoát sớm).
- **Tham số**: `IN p_asset_id BIGINT`, `IN p_request_id BIGINT`, `IN p_bakery_id BIGINT`.
- **Đọc**: `media_assets` (`FOR SHARE`). **Ghi**: không.
- **Quy tắc**: ảnh phải gắn với đúng yêu cầu và thuộc loại `design_proposal`, `customer_reference` hoặc `ai_generated` (loại `cake_photo` bị từ chối). Với `design_proposal`, `owner_bakery_id` phải là tiệm báo giá. Hai loại ảnh của khách thì tiệm dùng được dù không sở hữu.
- **Lỗi**: `DESIGN_ASSET_NOT_FOUND`, `DESIGN_ASSET_WRONG_REQUEST` (gồm cả ảnh sai loại), `DESIGN_ASSET_WRONG_BAKERY`.
- **Transaction/khóa**: như guard số 1.

## 5. `sp_guard_quote_content`

- **Vị trí**: P dòng 177-202.
- **Chức năng**: kiểm tra nội dung báo giá.
- **Tham số** (đều `IN`, theo thứ tự): `p_price DECIMAL(12,2)`, `p_size_description VARCHAR(100)`, `p_servings SMALLINT UNSIGNED`, `p_flavor VARCHAR(200)`, `p_decoration TEXT`, `p_estimated_pickup_at DATETIME(3)`, `p_valid_until DATETIME(3)`, `p_now DATETIME(3)`.
- **Đọc/ghi**: không đụng bảng nào.
- **Lỗi**: `INVALID_ARGUMENT` (thiếu giá, ngày, `flavor` hoặc `decoration` rỗng, thiếu cả kích thước lẫn số phần, hoặc `servings = 0`), `QUOTE_INVALID_PRICE` (giá <= 0), `QUOTE_INVALID_TIMES` (`valid_until` hoặc `estimated_pickup_at` <= `p_now`).
- **Không kiểm tra**: `p_note`, `p_inscription`, quan hệ giữa `valid_until` và `estimated_pickup_at`, quan hệ giữa `estimated_pickup_at` và `pickup_at` của yêu cầu.
- **Transaction/khóa**: không.

---

## 6. `sp_send_request_to_bakery`

- **Vị trí**: P dòng 208-263.
- **Chức năng**: khách gửi yêu cầu (`draft` hoặc `open`) cho một tiệm `active`; yêu cầu `draft` chuyển `open`.
- **Tham số** (đều `IN`, theo thứ tự): `p_request_id BIGINT`, `p_customer_id BIGINT`, `p_bakery_id BIGINT`.
- **Kết quả**: không có OUT, không có result set. Id của dòng `request_bakeries` mới không được trả về; nếu cần, truy vấn theo cặp (`design_request_id`, `bakery_id`).
- **Đọc**: `design_requests` (`FOR UPDATE`), `request_bakeries` (`FOR UPDATE`), `users`, `bakeries` (qua guard).
- **Ghi**: `INSERT request_bakeries` (`pending`, `sent_at`, `updated_at = v_now`); `UPDATE design_requests SET status='open'` nếu đang `draft`.
- **Lỗi** theo thứ tự kiểm tra: `INVALID_ARGUMENT`; `REQUEST_NOT_FOUND`; guard khách (`USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `USER_NOT_CUSTOMER`); `NOT_REQUEST_OWNER`; `REQUEST_NOT_OPEN` (yêu cầu không ở `draft`/`open`); `REQUEST_ALREADY_SENT`; guard tiệm (`BAKERY_NOT_FOUND`, `BAKERY_NOT_ACTIVE`); `REQUEST_PICKUP_IN_PAST` (`pickup_at <= v_now`).
- **Transaction**: tự `START TRANSACTION` ... `COMMIT`; handler rollback + `RESIGNAL`.
- **Đồng thời**: khóa yêu cầu rồi dòng `request_bakeries` theo cặp; hai lần gửi đồng thời cùng cặp xếp hàng tại khóa yêu cầu, lần sau gặp `REQUEST_ALREADY_SENT`. Lớp chặn cuối là `uq_request_bakeries_request_bakery`.
- **Lưu ý backend**: backend truyền `p_customer_id` từ claim. Yêu cầu phải đã tồn tại (INSERT `design_requests` là việc trực tiếp của backend, không có procedure). Mỗi lần gọi gửi một tiệm; gửi nhiều tiệm là nhiều lần gọi, mỗi lần một transaction độc lập, nên có thể thành công một phần. Có thể gọi lại an toàn: lần trùng báo `REQUEST_ALREADY_SENT`.

## 7. `sp_create_quote`

- **Vị trí**: P dòng 265-350.
- **Chức năng**: tiệm gửi báo giá đầu tiên, hoặc báo giá mới sau khi đã rút phiên bản trước.
- **Tham số theo thứ tự**:

  | # | Tên | Kiểu | Chiều |
  | --- | --- | --- | --- |
  | 1 | `p_request_id` | BIGINT | IN |
  | 2 | `p_bakery_id` | BIGINT | IN |
  | 3 | `p_actor_user_id` | BIGINT | IN |
  | 4 | `p_price` | DECIMAL(12,2) | IN |
  | 5 | `p_size_description` | VARCHAR(100) | IN |
  | 6 | `p_servings` | SMALLINT UNSIGNED | IN |
  | 7 | `p_flavor` | VARCHAR(200) | IN |
  | 8 | `p_decoration` | TEXT | IN |
  | 9 | `p_inscription` | VARCHAR(200) | IN |
  | 10 | `p_note` | TEXT | IN |
  | 11 | `p_estimated_pickup_at` | DATETIME(3) | IN |
  | 12 | `p_valid_until` | DATETIME(3) | IN |
  | 13 | `p_design_asset_id` | BIGINT | IN |
  | 14 | `p_quote_id` | BIGINT | **OUT** |

- **Kết quả**: `p_quote_id` = id báo giá mới (`LAST_INSERT_ID()`), hoặc NULL nếu lỗi. Không có result set.
- **Đọc**: `design_requests`, `request_bakeries`, `quotes` (đều `FOR UPDATE`), `users`, `bakery_members`, `bakeries`, `media_assets` (qua guard, `FOR SHARE`).
- **Ghi**: `INSERT quotes` (`status='submitted'`, `revision_no = MAX+1`, chuỗi được `TRIM`, chuỗi rỗng ở kích thước và chữ thành NULL); `UPDATE request_bakeries SET status='quoted', responded_at=COALESCE(responded_at, v_now)`.
- **Lỗi** theo thứ tự: `INVALID_ARGUMENT` (id NULL); `REQUEST_NOT_FOUND`; guard thành viên (`USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `NOT_BAKERY_MEMBER`); `REQUEST_NOT_SENT_TO_BAKERY`; `REQUEST_NOT_OPEN`; `REQUEST_DECLINED_BY_BAKERY`; `QUOTE_ALREADY_EXISTS`; guard tiệm (`BAKERY_NOT_FOUND`, `BAKERY_NOT_ACTIVE`); guard ảnh (`DESIGN_ASSET_*`); guard nội dung (`INVALID_ARGUMENT`, `QUOTE_INVALID_PRICE`, `QUOTE_INVALID_TIMES`).
- **Transaction**: tự mở và `COMMIT`; handler rollback + `RESIGNAL`.
- **Đồng thời**: khóa `design_requests → request_bakeries → quotes` của cặp yêu cầu-tiệm; hai nhân viên tạo đồng thời: một người thành công, người kia nhận `QUOTE_ALREADY_EXISTS`. Lớp chặn cuối là `uq_quotes_current`.
- **Lưu ý backend**: tiền tệ không phải tham số (luôn mặc định `VND`). Không gọi lại vô điều kiện: lần gọi thứ hai khi đã có phiên bản hiện hành báo `QUOTE_ALREADY_EXISTS`, không tạo trùng. Nếu OUT không đọc được sau khi bị ngắt kết nối, không biết báo giá có được tạo hay không; phải truy vấn lại **[cần xác minh cách xử lý idempotency]**.

## 8. `sp_revise_quote`

- **Vị trí**: P dòng 352-443.
- **Chức năng**: sửa báo giá bằng cách tạo phiên bản mới; phiên bản được sửa phải đang là `submitted`.
- **Tham số theo thứ tự**: `p_quote_id BIGINT`, `p_actor_user_id BIGINT`, `p_price DECIMAL(12,2)`, `p_size_description VARCHAR(100)`, `p_servings SMALLINT UNSIGNED`, `p_flavor VARCHAR(200)`, `p_decoration TEXT`, `p_inscription VARCHAR(200)`, `p_note TEXT`, `p_estimated_pickup_at DATETIME(3)`, `p_valid_until DATETIME(3)`, `p_design_asset_id BIGINT` (tất cả `IN`), rồi `p_new_quote_id BIGINT` (**OUT**). Tổng 13 tham số. So với `sp_create_quote`, không có `p_request_id`, `p_bakery_id` (suy ra từ báo giá).
- **Kết quả**: `p_new_quote_id` = id phiên bản mới, hoặc NULL nếu lỗi.
- **Đọc**: `quotes` (tra cứu ngoài transaction, rồi `FOR UPDATE`), `design_requests`, `request_bakeries` (`FOR UPDATE`; giá trị đọc không được dùng, chỉ để giữ khóa), các bảng của guard.
- **Ghi**: `UPDATE quotes SET status='superseded'` (phiên bản cũ), rồi `INSERT quotes` (phiên bản mới `revision_no = MAX+1`). Không đổi `request_bakeries`.
- **Lỗi** theo thứ tự: `INVALID_ARGUMENT`; `QUOTE_NOT_FOUND`; guard thành viên (`USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `NOT_BAKERY_MEMBER`); `QUOTE_ALREADY_ACCEPTED`; `REQUEST_NOT_OPEN`; `QUOTE_NOT_CURRENT` (phiên bản không còn `submitted`: `superseded`, `withdrawn`, `not_selected`); guard tiệm; guard ảnh; guard nội dung.
- **Transaction**: tự mở; handler rollback + `RESIGNAL`.
- **Đồng thời**: hai lần sửa cùng `p_quote_id`: lần sau chờ khóa yêu cầu, thấy `superseded`, nhận `QUOTE_NOT_CURRENT`. Hạ phiên bản cũ trước khi thêm phiên bản mới để thỏa `uq_quotes_current`.
- **Lưu ý backend**: backend phải truyền id phiên bản **hiện hành** mà giao diện đang hiển thị; nếu người dùng giữ id cũ sẽ nhận `QUOTE_NOT_CURRENT`. Procedure không kiểm tra `valid_until` của phiên bản cũ, nghĩa là sửa báo giá đã hết hạn vẫn được phép (phiên bản mới thì phải có `valid_until` trong tương lai) **[cần chốt nghiệp vụ]**. Không gọi lại tự động: lần gọi lặp sau khi đã thành công trả `QUOTE_NOT_CURRENT`.

## 9. `sp_withdraw_quote`

- **Vị trí**: P dòng 445-487.
- **Chức năng**: tiệm rút phiên bản hiện hành (`submitted`). Cho phép cả khi tiệm không còn `active`; thành viên phải còn `active`.
- **Tham số**: `IN p_quote_id BIGINT`, `IN p_actor_user_id BIGINT`.
- **Kết quả**: không.
- **Đọc**: `quotes` (tra cứu rồi `FOR UPDATE`), `design_requests` (`FOR UPDATE`; trạng thái được đọc nhưng không dùng), `users`, `bakery_members`.
- **Ghi**: `UPDATE quotes SET status='withdrawn', withdrawn_at, updated_at`.
- **Lỗi**: `INVALID_ARGUMENT`; `QUOTE_NOT_FOUND`; guard thành viên; `QUOTE_ALREADY_ACCEPTED`; `QUOTE_NOT_CURRENT`.
- **Transaction**: tự mở; handler rollback + `RESIGNAL`.
- **Khóa**: `design_requests` rồi `quotes`.
- **Lưu ý**: không kiểm tra trạng thái yêu cầu, không đổi `request_bakeries.status`. Sau khi rút, `request_bakeries.status` vẫn là `quoted` **[cần xác minh có phải ý định thiết kế không]**. Muốn báo giá lại thì gọi `sp_create_quote`.

## 10. `sp_accept_quote`

- **Vị trí**: P dòng 489-603.
- **Chức năng**: khách chọn báo giá và tạo đơn kèm bản chốt trong một transaction.
- **Tham số**: `IN p_quote_id BIGINT`, `IN p_customer_id BIGINT`, `OUT p_order_id BIGINT`.
- **Kết quả**: `p_order_id` = id đơn mới, hoặc NULL nếu lỗi.
- **Đọc**: `quotes` (tra cứu, rồi `FOR UPDATE`), `design_requests` (`FOR UPDATE`), `users`, `bakeries`, `media_assets` (`FOR SHARE`, hai lần).
- **Ghi**: `INSERT orders` (`pending_confirmation`, sao chép nội dung phiên bản và thông tin ảnh vào `agreed_*`); `UPDATE quotes` phiên bản chọn → `accepted`; các phiên bản `submitted` khác của cùng yêu cầu → `not_selected`; `UPDATE design_requests` → `ordered`.
- **Lỗi** theo thứ tự: `INVALID_ARGUMENT`; `QUOTE_NOT_FOUND`; guard khách (`USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `USER_NOT_CUSTOMER`); `NOT_REQUEST_OWNER`; `REQUEST_NOT_OPEN`; `QUOTE_NOT_AVAILABLE` (không còn `submitted`); guard tiệm (`BAKERY_NOT_FOUND`, `BAKERY_NOT_ACTIVE`); guard ảnh (`DESIGN_ASSET_*`); `QUOTE_EXPIRED` (`valid_until <= v_now`).
- **Transaction**: tự mở; handler rollback + `RESIGNAL`.
- **Đồng thời**: khóa yêu cầu rồi báo giá; mọi lần chọn cho cùng yêu cầu xếp hàng tại dòng yêu cầu. Lần chọn thứ hai (cùng hoặc khác báo giá) thấy yêu cầu `ordered` → `REQUEST_NOT_OPEN`. Báo giá hết hạn trong lúc chờ khóa bị từ chối vì `v_now` lấy sau khi giữ khóa. Lớp chặn cuối: `uq_orders_quote`, `uq_orders_design_request`.
- **Lưu ý backend**: không thay đổi `request_bakeries`. `QUOTE_NOT_AVAILABLE` và `REQUEST_NOT_OPEN` có thể xuất hiện khi người dùng bấm hai lần: nên coi đây là kết quả hợp lệ chứ không phải lỗi hệ thống, rồi truy vấn đơn hiện có của yêu cầu. Không retry mù: nếu OUT không đọc được, tìm đơn theo `design_request_id`.

## 11. `sp_bakery_decide_order`

- **Vị trí**: P dòng 605-669.
- **Chức năng**: thành viên `active` của đúng tiệm xác nhận (`confirm`) hoặc từ chối (`reject`) đơn đang `pending_confirmation`.
- **Tham số**: `IN p_order_id BIGINT`, `IN p_actor_user_id BIGINT`, `IN p_decision VARCHAR(10)` (`'confirm'` hoặc `'reject'`), `IN p_reason VARCHAR(500)`.
- **Kết quả**: không.
- **Đọc**: `orders` (tra cứu, rồi `FOR UPDATE`), `design_requests` (`FOR UPDATE`; trạng thái đọc nhưng không dùng), `users`, `bakery_members`, và `bakeries` chỉ khi `confirm`.
- **Ghi**: `confirm`: `UPDATE orders` (`confirmed`, `bakery_decided_at`, `bakery_decided_by_user_id`). `reject`: `UPDATE orders` (`rejected_by_bakery`, `bakery_decided_*`, `rejection_reason = p_reason`) và `UPDATE design_requests SET status='cancelled'`.
- **Lỗi**: `INVALID_ARGUMENT`; `INVALID_DECISION`; `ORDER_NOT_FOUND`; guard thành viên (`USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `NOT_BAKERY_MEMBER`); `ORDER_NOT_PENDING`; với `confirm`: `BAKERY_NOT_FOUND`, `BAKERY_NOT_ACTIVE`.
- **Transaction**: tự mở; handler rollback + `RESIGNAL`.
- **Khóa**: `design_requests` rồi `orders`.
- **Lưu ý**: `reject` được phép cả khi tiệm không `active`. `p_reason` không bị kiểm tra (có thể NULL hoặc rỗng) và bị bỏ qua khi `confirm`. Báo giá liên quan vẫn ở trạng thái `accepted` sau khi tiệm từ chối. Không có chức năng riêng cho "tiệm xác nhận khả năng làm bánh" ngoài hành động `confirm` này **[cần chốt nghiệp vụ]**.

---

## Khoảng trống: thao tác không có procedure

Các việc sau backend phải làm bằng câu lệnh trực tiếp (cần quyền trong `security/app_user_grants.sql`) hoặc chưa được hỗ trợ:

| Việc | Hiện trạng |
| --- | --- |
| Đăng ký, đăng nhập, đổi mật khẩu | Không có procedure; ghi trực tiếp `users` (script quyền cho INSERT/UPDATE) |
| Tạo, sửa yêu cầu, hủy yêu cầu | Không có procedure; ghi trực tiếp `design_requests` |
| Tạo `media_assets` | Không có procedure; chỉ INSERT |
| Quản lý tiệm, thành viên, mẫu bánh | Không có procedure |
| Tiệm đánh dấu đã xem hoặc từ chối yêu cầu (`request_bakeries` → `viewed`/`declined`) | Không có procedure; script quyền cho UPDATE vài cột |
| Đơn → `ready`, `picked_up`, `cancelled` | Không có procedure; script quyền cho UPDATE `status`, `picked_up_at`, `cancelled_at`, `updated_at` |
| Danh sách, chi tiết, tìm kiếm | Không có procedure; đọc bằng SELECT |

## Chung: lưu ý khi gọi từ backend

1. Backend dùng ADO.NET với MySqlConnector (xem `README.md`). Mỗi lần gọi mở một connection từ `IMySqlConnectionFactory` và không có transaction ứng dụng đang mở trên connection đó. Mỗi lần gọi là một transaction độc lập do procedure tự quản lý.
2. Bắt lỗi nghiệp vụ qua `MySqlException` với SQLSTATE `45000` và lấy mã từ `Message`. Lỗi khác (CHECK, UNIQUE, deadlock, lock wait timeout) đến với SQLSTATE khác. **[cần xác minh trên MySqlConnector: `SqlState`, `Number` và `Message` của lỗi `SIGNAL` trước khi viết mã ánh xạ lỗi]**.
3. Gọi bằng `MySqlCommand` với `CommandType.StoredProcedure`, parameter đúng thứ tự và kiểu, OUT parameter khai báo `ParameterDirection.Output`. **[cần xác minh bằng thử nghiệm trên database kiểm thử riêng, không trên Aiven hiện có]**.
4. SQL luôn dùng parameter. Kiểu `MySqlConnector` chỉ nằm trong Infrastructure; Application và Domain không tham chiếu.5. Id người thực hiện lấy từ claim đã xác thực, không từ body request.
6. Chuyển mã lỗi nghiệp vụ sang HTTP (404, 403, 409, 422) ở Api; bảng ánh xạ cần được nhóm chốt riêng.
