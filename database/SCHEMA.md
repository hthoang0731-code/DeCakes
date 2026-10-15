# Schema `decakes_db` (phiên bản 2)

Tài liệu này được lập từ chính script SQL trong thư mục này, không từ mô tả tóm tắt.

- Nguồn chính: `install/01_schema.sql` (viết tắt **S**, kèm số dòng). Procedure: `install/02_procedures.sql` (xem [STORED_PROCEDURES.md](STORED_PROCEDURES.md)).
- Nguồn tham chiếu thêm: `security/app_user_grants.sql`. Bản gốc `docs/database-design.md` nằm trong ZIP nguồn, chưa được đưa vào repository này.
- Kết quả kiểm thử ghi trong `TEST_REPORT.md` của ZIP nguồn **không** phải kết quả chạy trong nhiệm vụ này. Tài liệu này chỉ đọc script, chưa kết nối database.

## Tổng quan

| Loại | Số lượng | Tên |
| --- | --- | --- |
| Bảng nghiệp vụ | 9 | `users`, `bakeries`, `bakery_members`, `cakes`, `design_requests`, `request_bakeries`, `media_assets`, `quotes`, `orders` |
| Bảng hệ thống | 1 | `schema_migrations` |
| Trigger / view / function / event | 0 | Không có trong script cài đặt (đã tìm `CREATE TRIGGER/VIEW/EVENT/FUNCTION`) |
| Stored procedure | 11 | Xem [STORED_PROCEDURES.md](STORED_PROCEDURES.md) |

Quy ước chung (S dòng 15-27):

- Engine InnoDB, utf8mb4, collation `utf8mb4_0900_ai_ci`.
- Thời gian là `DATETIME(3)` theo UTC, không dùng `TIMESTAMP`. Default của `created_at`/`updated_at` là `UTC_TIMESTAMP(3)`. `updated_at` không tự cập nhật, do procedure hoặc ứng dụng gán.
- Tiền: `DECIMAL(12,2)`, chỉ có `'VND'` (CHECK).
- Trạng thái: `VARCHAR` kèm CHECK, không dùng ENUM.
- Khóa ngoại không khai báo `ON DELETE`/`ON UPDATE`, nghĩa là mặc định `NO ACTION` (chặn xóa/đổi dòng cha khi còn dòng con). Không có `CASCADE` hay `SET NULL` ở đâu cả.
- Mọi bảng có PRIMARY KEY. Tất cả khóa ngoại trỏ tới PRIMARY KEY hoặc UNIQUE đầy đủ cột.
- Cột `is_demo` đánh dấu dữ liệu demo.

## Quan hệ giữa các bảng

```
users ──< bakery_members >── bakeries ──< cakes
  │                              │            │
  │                              └──< request_bakeries >── design_requests >── users (customer)
  │                                        │                      │  \
  │                                        │                      │   └── cakes (inspired_by, tùy chọn)
  │                                        v                      │
  │                                     quotes (mỗi dòng = 1 phiên bản) ──> media_assets (ảnh thiết kế, tùy chọn)
  │                                        │
  └────────────────────────────────────> orders (bản chốt agreed_*)
```

Các khóa ngoại, tất cả `NO ACTION`/`NO ACTION` (mặc định):

| Khóa ngoại | Từ | Tới | Ý nghĩa |
| --- | --- | --- | --- |
| `fk_bakery_members_bakery` | `bakery_members.bakery_id` | `bakeries.id` | |
| `fk_bakery_members_user` | `bakery_members.user_id` | `users.id` | |
| `fk_cakes_bakery` | `cakes.bakery_id` | `bakeries.id` | |
| `fk_design_requests_customer` | `design_requests.customer_id` | `users.id` | |
| `fk_design_requests_cake` | `design_requests.inspired_by_cake_id` | `cakes.id` | Yêu cầu lấy cảm hứng từ mẫu bánh (nullable) |
| `fk_request_bakeries_request` | `request_bakeries.design_request_id` | `design_requests.id` | |
| `fk_request_bakeries_bakery` | `request_bakeries.bakery_id` | `bakeries.id` | |
| `fk_media_assets_uploader` | `media_assets.uploaded_by_user_id` | `users.id` | |
| `fk_media_assets_bakery` | `media_assets.owner_bakery_id` | `bakeries.id` | |
| `fk_media_assets_request` | `media_assets.design_request_id` | `design_requests.id` | |
| `fk_media_assets_cake_bakery` | `media_assets(cake_id, owner_bakery_id)` | `cakes(id, bakery_id)` | Ảnh mẫu bánh phải thuộc đúng tiệm sở hữu mẫu |
| `fk_media_assets_request_bakery` | `media_assets(design_request_id, owner_bakery_id)` | `request_bakeries(design_request_id, bakery_id)` | Ảnh đề xuất của tiệm: tiệm phải đã nhận yêu cầu. Bị bỏ qua khi một cột NULL |
| `fk_quotes_request_bakery` | `quotes(design_request_id, bakery_id)` | `request_bakeries(design_request_id, bakery_id)` | Chỉ tiệm đã nhận yêu cầu mới báo giá được |
| `fk_quotes_created_by_member` | `quotes(bakery_id, created_by_user_id)` | `bakery_members(bakery_id, user_id)` | Người tạo phải là thành viên (đã từng) của tiệm |
| `fk_quotes_confirmed_design` | `quotes(confirmed_design_asset_id, design_request_id)` | `media_assets(id, design_request_id)` | Ảnh thiết kế cùng yêu cầu |
| `fk_orders_quote` | `orders(quote_id, design_request_id, bakery_id)` | `quotes(id, design_request_id, bakery_id)` | Đơn khớp yêu cầu và tiệm của báo giá |
| `fk_orders_request_customer` | `orders(design_request_id, customer_id)` | `design_requests(id, customer_id)` | Khách của đơn là chủ yêu cầu |
| `fk_orders_bakery` | `orders.bakery_id` | `bakeries.id` | |
| `fk_orders_customer` | `orders.customer_id` | `users.id` | |
| `fk_orders_decided_by_member` | `orders(bakery_id, bakery_decided_by_user_id)` | `bakery_members(bakery_id, user_id)` | Người quyết định là thành viên của tiệm |
| `fk_orders_agreed_design` | `orders.agreed_design_asset_id` | `media_assets.id` | |

Lý do không dùng CASCADE/SET NULL: MySQL cấm các hành động này trên cột nằm trong CHECK, và prototype ưu tiên đổi trạng thái thay vì xóa (S dòng 22-24). Các script không cấp quyền DELETE cho tài khoản backend (`security/app_user_grants.sql`).

## Một yêu cầu tạo tối đa một đơn: database bảo đảm thế nào

Có hai lớp, cả hai ở `orders` (S dòng 396-397):

1. `UNIQUE (design_request_id)` tên `uq_orders_design_request`: mỗi yêu cầu có tối đa một dòng `orders`. Đây là lớp chặn cuối, áp dụng kể cả khi ghi trực tiếp bằng INSERT.
2. `UNIQUE (quote_id)` tên `uq_orders_quote`: mỗi báo giá tạo tối đa một đơn.

Ở tầng procedure, `sp_accept_quote` khóa `design_requests FOR UPDATE` trước, nên các lần chọn đồng thời xếp hàng. Lần sau thấy yêu cầu đã `ordered` và trả `REQUEST_NOT_OPEN`.

Hệ quả (điều này đúng theo script, không phải suy đoán): ràng buộc `UNIQUE (design_request_id)` không phân biệt trạng thái đơn. Khi tiệm từ chối, đơn `rejected_by_bakery` vẫn tồn tại, nên yêu cầu đó không thể có đơn thứ hai. Vì vậy `sp_bakery_decide_order` đặt yêu cầu thành `cancelled` và khách phải tạo yêu cầu mới.

## Bảng `schema_migrations` (bảng hệ thống)

Ghi phiên bản schema. Không phải bảng nghiệp vụ, không cần model nghiệp vụ.

| Cột | Kiểu | Null | Default | Ghi chú |
| --- | --- | --- | --- | --- |
| `version` | INT | không | | PK `pk_schema_migrations` |
| `description` | VARCHAR(200) | không | | |
| `applied_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` | |

Script cài mới ghi 2 dòng: phiên bản 1 và 2 (S dòng 436-438). Nguồn: `CREATE TABLE schema_migrations`.

## Bảng nghiệp vụ

### 1. `users`

Mục đích: tài khoản đăng nhập website (khách, nhân viên tiệm, quản trị viên). Không phải tài khoản MySQL. Nguồn: S dòng 50-70.

| Cột | Kiểu | Null | Default | Ghi chú |
| --- | --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | | PK |
| `email` | VARCHAR(255) | không | | UNIQUE |
| `password_hash` | VARCHAR(255) | có | NULL | Chỉ lưu hash. NULL = chưa đặt mật khẩu, không đăng nhập được (tài khoản demo) |
| `full_name` | VARCHAR(150) | không | | |
| `phone` | VARCHAR(20) | có | NULL | |
| `system_role` | VARCHAR(20) | không | `'customer'` | |
| `status` | VARCHAR(20) | không | `'active'` | |
| `is_demo` | TINYINT(1) | không | 0 | |
| `created_at`, `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` | |

- PK: `pk_users (id)`. UNIQUE: `uq_users_email (email)`.
- CHECK: `chk_users_email` (`email LIKE '%_@_%'`); `chk_users_system_role` in (`customer`, `bakery`, `admin`); `chk_users_status` in (`active`, `disabled`); `chk_users_is_demo` in (0, 1).
- FK ra: không. Index: chỉ index của PK và UNIQUE.

### 2. `bakeries`

Mục đích: hồ sơ tiệm bánh. Chỉ tiệm `active` nhận yêu cầu mới, gửi/sửa báo giá, được khách chọn và xác nhận đơn. Nguồn: S dòng 77-96.

| Cột | Kiểu | Null | Default |
| --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | |
| `name` | VARCHAR(150) | không | |
| `slug` | VARCHAR(160) | không | |
| `description` | TEXT | có | NULL |
| `address_line` | VARCHAR(255) | có | NULL |
| `city` | VARCHAR(100) | có | NULL |
| `phone` | VARCHAR(20) | có | NULL |
| `profile_status` | VARCHAR(20) | không | `'draft'` |
| `is_demo` | TINYINT(1) | không | 0 |
| `created_at`, `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` |

- PK `pk_bakeries (id)`. UNIQUE `uq_bakeries_slug (slug)`.
- CHECK: `chk_bakeries_profile_status` in (`draft`, `pending_review`, `active`, `suspended`); `chk_bakeries_is_demo`.
- Index: `ix_bakeries_profile_status (profile_status)`.

### 3. `bakery_members`

Mục đích: tài khoản nào làm việc cho tiệm nào. Không xóa dòng khi nhân viên nghỉ, đổi `status = 'removed'`. Nguồn: S dòng 103-117.

| Cột | Kiểu | Null | Default |
| --- | --- | --- | --- |
| `bakery_id` | BIGINT | không | |
| `user_id` | BIGINT | không | |
| `member_role` | VARCHAR(20) | không | `'staff'` |
| `status` | VARCHAR(20) | không | `'active'` |
| `created_at`, `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` |

- PK ghép `pk_bakery_members (bakery_id, user_id)`.
- FK: `fk_bakery_members_bakery`, `fk_bakery_members_user`.
- CHECK: `chk_bakery_members_role` in (`owner`, `staff`); `chk_bakery_members_status` in (`active`, `removed`).
- Index: `ix_bakery_members_user (user_id)`.
- Lưu ý persistence: khóa ghép, không có cột `id`.

### 4. `cakes`

Mục đích: mẫu bánh của tiệm (catalog). Nguồn: S dòng 122-146.

| Cột | Kiểu | Null | Default |
| --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | |
| `bakery_id` | BIGINT | không | |
| `name` | VARCHAR(150) | không | |
| `description` | TEXT | có | NULL |
| `flavor` | VARCHAR(200) | có | NULL |
| `size_label` | VARCHAR(100) | có | NULL |
| `reference_price` | DECIMAL(12,2) | có | NULL |
| `currency` | CHAR(3) | không | `'VND'` |
| `visibility` | VARCHAR(20) | không | `'draft'` |
| `is_demo` | TINYINT(1) | không | 0 |
| `created_at`, `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` |

- PK `pk_cakes (id)`. UNIQUE `uq_cakes_id_bakery (id, bakery_id)` (đích của FK ghép từ `media_assets`).
- FK: `fk_cakes_bakery`.
- CHECK: `chk_cakes_reference_price` (NULL hoặc >= 0); `chk_cakes_currency` = `VND`; `chk_cakes_visibility` in (`draft`, `published`, `hidden`); `chk_cakes_is_demo`.
- Index: `ix_cakes_bakery_visibility (bakery_id, visibility)`.

### 5. `design_requests`

Mục đích: yêu cầu đặt bánh của khách. Vòng đời `draft → open → ordered | cancelled`. Khách sửa được khi còn `draft`/`open`; đơn đã tạo không phụ thuộc các cột này (có bản chốt `agreed_*`). Nguồn: S dòng 154-190.

| Cột | Kiểu | Null | Default | Ghi chú |
| --- | --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | | |
| `customer_id` | BIGINT | không | | |
| `inspired_by_cake_id` | BIGINT | có | NULL | Mẫu bánh cảm hứng |
| `title` | VARCHAR(200) | không | | |
| `idea_description` | TEXT | không | | |
| `budget_min` | DECIMAL(12,2) | có | NULL | |
| `budget_max` | DECIMAL(12,2) | không | | |
| `currency` | CHAR(3) | không | `'VND'` | |
| `size_description` | VARCHAR(100) | có | NULL | |
| `servings` | SMALLINT UNSIGNED | có | NULL | |
| `flavor` | VARCHAR(200) | có | NULL | |
| `inscription_text` | VARCHAR(200) | có | NULL | Chữ trên bánh (thêm ở v2) |
| `pickup_at` | DATETIME(3) | không | | UTC |
| `status` | VARCHAR(20) | không | `'draft'` | |
| `is_demo` | TINYINT(1) | không | 0 | |
| `created_at`, `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` | |

- PK `pk_design_requests (id)`. UNIQUE `uq_design_requests_id_customer (id, customer_id)` (đích FK ghép từ `orders`).
- FK: `fk_design_requests_customer`, `fk_design_requests_cake`.
- CHECK: `chk_design_requests_budget` (`budget_max > 0` và nếu có `budget_min` thì `0 <= budget_min <= budget_max`); `chk_design_requests_currency`; `chk_design_requests_servings` (NULL hoặc > 0); `chk_design_requests_pickup` (`pickup_at > created_at`); `chk_design_requests_status` in (`draft`, `open`, `ordered`, `cancelled`); `chk_design_requests_is_demo`.
- Index: `ix_design_requests_customer_status (customer_id, status)`, `ix_design_requests_status_pickup (status, pickup_at)`.
- Ảnh tham khảo của khách nằm ở `media_assets` (không có cột ảnh ở bảng này). Không có cột riêng cho hương vị nhiều lựa chọn hay ảnh; chỉ một `flavor` dạng chuỗi.

### 6. `request_bakeries`

Mục đích: yêu cầu được gửi đến những tiệm nào. Trạng thái `pending → viewed → quoted | declined`. Nguồn: S dòng 197-218.

| Cột | Kiểu | Null | Default |
| --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | |
| `design_request_id` | BIGINT | không | |
| `bakery_id` | BIGINT | không | |
| `status` | VARCHAR(20) | không | `'pending'` |
| `decline_reason` | VARCHAR(500) | có | NULL |
| `sent_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` |
| `responded_at` | DATETIME(3) | có | NULL |
| `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` |

- PK `pk_request_bakeries (id)`. UNIQUE `uq_request_bakeries_request_bakery (design_request_id, bakery_id)`: mỗi tiệm nhận một lần cho mỗi yêu cầu; cũng là đích FK ghép từ `quotes` và `media_assets`.
- FK: `fk_request_bakeries_request`, `fk_request_bakeries_bakery`.
- CHECK: `chk_request_bakeries_status` in (`pending`, `viewed`, `quoted`, `declined`); `chk_request_bakeries_responded` (nếu `quoted`/`declined` thì `responded_at` NOT NULL).
- Index: `ix_request_bakeries_bakery_status (bakery_id, status)`.
- Thêm dòng chỉ qua `sp_send_request_to_bakery`. Các chuyển trạng thái `viewed` và `declined` **không có procedure** (xem [STORED_PROCEDURES.md](STORED_PROCEDURES.md), phần khoảng trống).

### 7. `media_assets`

Mục đích: ảnh lưu ngoài database; chỉ có URL, loại và chủ sở hữu. Ảnh bất biến: thay ảnh = thêm dòng mới (`version_no` tăng), không UPDATE. Nguồn: S dòng 233-280.

| Cột | Kiểu | Null | Default |
| --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | |
| `asset_kind` | VARCHAR(30) | không | |
| `storage_url` | VARCHAR(1024) | không | |
| `mime_type` | VARCHAR(50) | không | |
| `width_px`, `height_px` | INT UNSIGNED | có | NULL |
| `uploaded_by_user_id` | BIGINT | không | |
| `owner_bakery_id` | BIGINT | có | NULL |
| `cake_id` | BIGINT | có | NULL |
| `design_request_id` | BIGINT | có | NULL |
| `version_no` | INT UNSIGNED | có | NULL |
| `caption` | VARCHAR(255) | có | NULL |
| `is_demo` | TINYINT(1) | không | 0 |
| `created_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` |

- PK `pk_media_assets (id)`. UNIQUE `uq_media_assets_id_request (id, design_request_id)` (đích FK ghép từ `quotes`).
- FK: xem bảng quan hệ ở trên (5 khóa).
- CHECK:
  - `chk_media_assets_kind` in (`cake_photo`, `customer_reference`, `ai_generated`, `design_proposal`).
  - `chk_media_assets_owner_by_kind`: `cake_photo` cần `cake_id` + `owner_bakery_id`, không có `design_request_id`; `customer_reference`/`ai_generated` cần `design_request_id`, không có `owner_bakery_id`/`cake_id`; `design_proposal` cần `design_request_id` + `owner_bakery_id`, không có `cake_id`.
  - `chk_media_assets_url` (`storage_url LIKE 'https://%'`); `chk_media_assets_mime` in (`image/jpeg`, `image/png`, `image/webp`); `chk_media_assets_version` (NULL hoặc >= 1); `chk_media_assets_is_demo`.
- Index: `ix_media_assets_cake (cake_id)`, `ix_media_assets_request_kind (design_request_id, asset_kind)`.
- Loại `ai_generated` đã có sẵn trong schema; chức năng AI triển khai sau.

### 8. `quotes`

Mục đích: báo giá của một tiệm cho một yêu cầu. **Mỗi dòng là một phiên bản.** Trạng thái `submitted → accepted | not_selected | withdrawn | superseded`. Nội dung một phiên bản không bị sửa; sửa báo giá = `sp_revise_quote` (phiên bản cũ thành `superseded`, thêm dòng mới `revision_no + 1`). Nguồn: S dòng 298-349.

| Cột | Kiểu | Null | Default | Ghi chú |
| --- | --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | | |
| `design_request_id` | BIGINT | không | | |
| `bakery_id` | BIGINT | không | | |
| `revision_no` | INT UNSIGNED | không | 1 | |
| `price` | DECIMAL(12,2) | không | | |
| `currency` | CHAR(3) | không | `'VND'` | |
| `size_description` | VARCHAR(100) | có | NULL | |
| `servings` | SMALLINT UNSIGNED | có | NULL | |
| `flavor` | VARCHAR(200) | không | | |
| `decoration_description` | TEXT | không | | |
| `inscription_text` | VARCHAR(200) | có | NULL | |
| `note` | TEXT | có | NULL | |
| `estimated_pickup_at` | DATETIME(3) | không | | UTC |
| `valid_until` | DATETIME(3) | không | | UTC |
| `confirmed_design_asset_id` | BIGINT | có | NULL | |
| `created_by_user_id` | BIGINT | không | | |
| `status` | VARCHAR(20) | không | `'submitted'` | |
| `current_slot` | TINYINT | có | | **Cột sinh tự động STORED**: `CASE WHEN status IN ('submitted','accepted') THEN 1 ELSE NULL END` |
| `withdrawn_at` | DATETIME(3) | có | NULL | |
| `created_at`, `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` | |

- PK `pk_quotes (id)`.
- UNIQUE: `uq_quotes_revision (design_request_id, bakery_id, revision_no)`; `uq_quotes_current (design_request_id, bakery_id, current_slot)` — vì `current_slot` NULL khi không hiệu lực và MySQL cho phép nhiều NULL trong UNIQUE, mỗi cặp yêu cầu-tiệm có tối đa một phiên bản hiện hành; `uq_quotes_id_request_bakery (id, design_request_id, bakery_id)` (đích FK ghép từ `orders`).
- FK: `fk_quotes_request_bakery`, `fk_quotes_created_by_member`, `fk_quotes_confirmed_design`.
- CHECK: `chk_quotes_price` (> 0); `chk_quotes_currency`; `chk_quotes_revision` (>= 1); `chk_quotes_size` (`size_description` hoặc `servings` phải có); `chk_quotes_servings`; `chk_quotes_valid_until` (`valid_until > created_at`); `chk_quotes_estimated_pickup` (`estimated_pickup_at > created_at`); `chk_quotes_status` in (`submitted`, `withdrawn`, `superseded`, `accepted`, `not_selected`); `chk_quotes_withdrawn` (`status = 'withdrawn'` khi và chỉ khi `withdrawn_at` NOT NULL).
- Index: `ix_quotes_request_status (design_request_id, status)`, `ix_quotes_bakery_status (bakery_id, status)`.
- "Hết hạn" **không phải trạng thái lưu**: hết hạn khi `valid_until <=` thời điểm kiểm tra; do `sp_accept_quote` kiểm tra sau khi giữ khóa.
- Quy tắc "ảnh đề xuất phải của chính tiệm báo giá" **không** có trong FK, chỉ được kiểm tra trong procedure (`sp_guard_quote_design`).
- Lưu ý persistence: `current_slot` là cột computed, không được gán giá trị từ ứng dụng.

### 9. `orders`

Mục đích: đơn tạo khi khách chọn báo giá. Nguồn: S dòng 364-431.

Trạng thái: `pending_confirmation → confirmed → ready → picked_up`; `pending_confirmation → rejected_by_bakery`; `pending_confirmation | confirmed → cancelled`.

| Cột | Kiểu | Null | Default | Ghi chú |
| --- | --- | --- | --- | --- |
| `id` | BIGINT AUTO_INCREMENT | không | | |
| `quote_id` | BIGINT | không | | |
| `design_request_id` | BIGINT | không | | |
| `customer_id` | BIGINT | không | | |
| `bakery_id` | BIGINT | không | | |
| `agreed_quote_revision_no` | INT UNSIGNED | không | | Bản chốt |
| `agreed_price` | DECIMAL(12,2) | không | | Bản chốt |
| `currency` | CHAR(3) | không | `'VND'` | |
| `agreed_pickup_at` | DATETIME(3) | không | | Bản chốt, UTC |
| `agreed_size_description` | VARCHAR(100) | có | NULL | |
| `agreed_servings` | SMALLINT UNSIGNED | có | NULL | |
| `agreed_flavor` | VARCHAR(200) | không | | |
| `agreed_decoration_description` | TEXT | không | | |
| `agreed_inscription_text` | VARCHAR(200) | có | NULL | |
| `agreed_note` | TEXT | có | NULL | |
| `agreed_design_asset_id` | BIGINT | có | NULL | |
| `agreed_design_asset_kind` | VARCHAR(30) | có | NULL | |
| `agreed_design_version_no` | INT UNSIGNED | có | NULL | |
| `agreed_design_url` | VARCHAR(1024) | có | NULL | Sao chép URL ảnh |
| `status` | VARCHAR(30) | không | `'pending_confirmation'` | |
| `bakery_decided_at` | DATETIME(3) | có | NULL | |
| `bakery_decided_by_user_id` | BIGINT | có | NULL | |
| `rejection_reason` | VARCHAR(500) | có | NULL | |
| `picked_up_at` | DATETIME(3) | có | NULL | |
| `cancelled_at` | DATETIME(3) | có | NULL | |
| `created_at`, `updated_at` | DATETIME(3) | không | `UTC_TIMESTAMP(3)` | |

- PK `pk_orders (id)`. UNIQUE: `uq_orders_quote (quote_id)`, `uq_orders_design_request (design_request_id)`.
- FK: `fk_orders_quote`, `fk_orders_request_customer`, `fk_orders_bakery`, `fk_orders_customer`, `fk_orders_decided_by_member`, `fk_orders_agreed_design`.
- CHECK:
  - `chk_orders_status` in (`pending_confirmation`, `confirmed`, `rejected_by_bakery`, `ready`, `picked_up`, `cancelled`); `chk_orders_price` (> 0); `chk_orders_currency`; `chk_orders_agreed_size` (`agreed_size_description` hoặc `agreed_servings`).
  - `chk_orders_agreed_design`: hoặc cả ba cột ảnh (`id`, `url`, `kind`) cùng NULL, hoặc cả ba cùng có và `kind` in (`customer_reference`, `ai_generated`, `design_proposal`).
  - `chk_orders_decision`: `pending_confirmation` thì chưa có `bakery_decided_*`; `confirmed`/`rejected_by_bakery`/`ready`/`picked_up` thì phải có cả `bakery_decided_at` và `bakery_decided_by_user_id`; `cancelled` thì không ràng buộc. Đây là cơ chế bảo vệ "tiệm phải xác nhận trước khi đơn sang `confirmed`/`ready`/`picked_up`".
  - `chk_orders_picked_up` (`status = 'picked_up'` khi và chỉ khi `picked_up_at` NOT NULL); `chk_orders_cancelled` (tương tự với `cancelled_at`).
- Index: `ix_orders_bakery_status (bakery_id, status)`, `ix_orders_customer_status (customer_id, status)`.
- Các cột `agreed_*` là bản chốt sao chép từ báo giá và ảnh lúc khách chọn. Procedure không UPDATE chúng, và tài khoản `decakes_app` không có quyền UPDATE cột này.
- `rejection_reason` không có CHECK ràng buộc với trạng thái.

## Trạng thái tổng hợp

| Đối tượng | Giá trị (theo CHECK) | Chuyển trạng thái do procedure thực hiện |
| --- | --- | --- |
| `users.status` | `active`, `disabled` | không có procedure |
| `users.system_role` | `customer`, `bakery`, `admin` | không có procedure |
| `bakeries.profile_status` | `draft`, `pending_review`, `active`, `suspended` | không có procedure |
| `bakery_members.status` | `active`, `removed` | không có procedure |
| `cakes.visibility` | `draft`, `published`, `hidden` | không có procedure |
| `design_requests.status` | `draft`, `open`, `ordered`, `cancelled` | `draft→open` (`sp_send_request_to_bakery`), `open→ordered` (`sp_accept_quote`), `→cancelled` (`sp_bakery_decide_order` khi từ chối). Tạo `draft` và khách hủy: không có procedure |
| `request_bakeries.status` | `pending`, `viewed`, `quoted`, `declined` | `pending→quoted` (`sp_create_quote`). `viewed`, `declined`: không có procedure |
| `quotes.status` | `submitted`, `withdrawn`, `superseded`, `accepted`, `not_selected` | cả 5 trạng thái đều do procedure |
| `orders.status` | `pending_confirmation`, `confirmed`, `rejected_by_bakery`, `ready`, `picked_up`, `cancelled` | tạo `pending_confirmation` (`sp_accept_quote`), `confirmed`/`rejected_by_bakery` (`sp_bakery_decide_order`). `ready`, `picked_up`, `cancelled`: không có procedure |

## Quyền của tài khoản backend (tùy chọn)

`security/app_user_grants.sql` cấp cho user `decakes_app` (script mẫu, chưa biết đã áp dụng trên Aiven hay chưa, cần xác minh):

- `SELECT` và `EXECUTE` trên toàn `decakes_db`.
- `INSERT, UPDATE` trên `users`, `bakeries`, `bakery_members`, `cakes`, `design_requests`.
- `INSERT` trên `media_assets` (bất biến).
- `UPDATE (status, decline_reason, responded_at, updated_at)` trên `request_bakeries`.
- `quotes`: đọc được (SELECT) nhờ quyền đọc toàn database, nhưng không có INSERT/UPDATE/DELETE trực tiếp; mọi thay đổi chỉ qua procedure.
- `orders`: chỉ `UPDATE (status, picked_up_at, cancelled_at, updated_at)`.

Procedure mặc định chạy `SQL SECURITY DEFINER` (không khai báo trong script, là mặc định của MySQL), nên chúng ghi được vào `quotes` và `orders` dù tài khoản gọi không có quyền.

## Khác biệt giữa SQL và quy tắc nghiệp vụ nêu trong yêu cầu

Xem phần đối chiếu trong [../docs/BACKEND_DATABASE_PLAN.md](../docs/BACKEND_DATABASE_PLAN.md).
