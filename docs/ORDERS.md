# Đơn hàng — phạm vi và hợp đồng hiện tại

Ngày 02/11/2026, nhánh `feature/orders`. Gồm: khách chọn báo giá tạo đơn, đọc bản chốt, tiệm xác nhận hoặc từ chối. **Chưa có** ready, picked_up, hủy đơn, deadline tự động, thanh toán tiền bánh, chat, AI.

> **Migration V006 mới chỉ được kiểm thử trên MySQL 8.4 disposable local. Chưa áp dụng lên Aiven.** Trên database chưa có V006 (Aiven hiện ở version 5), API đọc đơn vẫn chạy; accept và decision trả 503 `ORDER_WORKFLOW_NOT_READY` và không gọi procedure.

## Quy tắc nghiệp vụ đã duyệt

- Gửi báo giá là cam kết sơ bộ; tiệm xác nhận đơn sau khi khách chọn là bước chốt cuối.
- Một yêu cầu tạo tối đa một đơn, kể cả khi đơn bị tiệm từ chối.
- Khách chỉ chọn được báo giá `submitted`, chưa hết hạn, thuộc yêu cầu `open` của chính mình. Giờ nhận của phiên bản phải còn ở tương lai, kiểm tra bằng giờ database **sau khi giữ đủ khóa**.
- Báo giá được vượt ngân sách và có giờ nhận khác yêu cầu; đơn chốt đúng điều kiện của phiên bản được chọn.
- Tiệm chỉ confirm/reject đơn `pending_confirmation`. Owner và staff active có cùng quyền.
- Reject bắt buộc có lý do. Đơn thành `rejected_by_bakery`, yêu cầu thành `cancelled`; không mở lại yêu cầu, không chọn được báo giá khác (báo giá đã chọn vẫn `accepted`).
- Khi giờ hiện tại >= `agreed_pickup_at`: chặn confirm, vẫn cho reject.
- Confirm cần tiệm `active`. Giữ hành vi SQL hiện có: gói hết hạn không chặn xử lý đơn đã tạo; reject vẫn được khi tiệm `suspended`. Tạo đơn mới cần tiệm `active` và gói còn hạn (V005).

## Endpoint

| Endpoint | Quyền | Kết quả |
| --- | --- | --- |
| `POST /api/design-requests/{requestId}/quotes/{quoteId}/accept` | Customer, **email đã xác minh**, CSRF | 201, `Location: /api/orders/{id}`, body là bản chốt |
| `GET /api/orders` | Customer | Trang đơn của khách |
| `GET /api/orders/{orderId}` | Customer | Một đơn của khách |
| `GET /api/design-requests/{requestId}/order` | Customer | Đơn của một yêu cầu (dùng để đối chiếu sau lỗi mạng) |
| `GET /api/bakery-workspace/bakeries/{bakeryId}/orders` | Bakery, thành viên active của đúng tiệm | Trang đơn của tiệm |
| `GET /api/bakery-workspace/bakeries/{bakeryId}/orders/{orderId}` | Bakery, thành viên active của đúng tiệm | Một đơn của tiệm |
| `POST /api/bakery-workspace/bakeries/{bakeryId}/orders/{orderId}/decision` | Bakery, thành viên active của đúng tiệm, CSRF | 200, body là đơn đọc lại |

- Cookie phiên được kiểm tra lại mỗi request; CSRF bắt buộc cho POST (filter toàn cục). Actor lấy từ claim; `customerId`/`actorUserId` trong body bị bỏ qua.
- Thao tác Bakery **không** yêu cầu email đã xác minh trong đợt này.
- Danh sách nhận `status`, `page`, `pageSize` theo `Paging`: mặc định trang 1, cỡ 20, tối đa 100. `status` thuộc mọi giá trị của `chk_orders_status`: `pending_confirmation`, `confirmed`, `rejected_by_bakery`, `ready`, `picked_up`, `cancelled`. Sắp xếp `created_at DESC, id DESC`. Tham số sai trả 400 trước khi truy vấn. Kết quả `{ items, page, pageSize, totalCount }`.
- Thao tác lặp hoặc sai trạng thái trả 409, không bao giờ coi là thành công.

### Body quyết định

`{ "decision": "confirm" | "reject", "reason": "..." }`

- `decision` phải đúng `confirm` hoặc `reject` (phân biệt hoa thường, không trim).
- `reject`: `reason` bắt buộc. Trim, chuẩn hóa NFC, tối đa 500 ký tự Unicode (VARCHAR(500) utf8mb4), cho phép xuống dòng/tab, không ký tự điều khiển khác, không UTF-16 lỗi.
- `confirm`: không nhận lý do. `reason` null, rỗng hoặc chỉ khoảng trắng coi như không gửi; có nội dung thì 400 (không âm thầm bỏ qua). Procedure không ghi lý do khi confirm.

## Bản chốt và cách ly dữ liệu

Body đơn: `id, requestId, quoteId, bakeryId, bakeryName, status, agreedQuoteRevisionNo, agreedPrice, currency, agreedPickupAt, agreedSizeDescription, agreedServings, agreedFlavor, agreedDecorationDescription, agreedInscriptionText, agreedNote, agreedDesignAssetId, agreedDesignAssetKind, agreedDesignVersionNo, bakeryDecidedAt, rejectionReason, pickedUpAt, cancelledAt, createdAt, updatedAt`.

- Nội dung đọc từ `orders.agreed_*`, không dựng lại từ báo giá, yêu cầu hay ảnh hiện tại. Sửa nguồn sau khi chốt không đổi đơn (đã kiểm thử bằng UPDATE trực tiếp lên quotes/media_assets/design_requests).
- `bakeryName` là tên hiện tại của tiệm, không thuộc bản chốt.
- Không trả email, điện thoại, `customer_id`, password hash, `bakery_decided_by_user_id`. Chưa trả `agreed_design_url` vì cơ chế phục vụ ảnh riêng tư chưa chốt; chỉ trả id/loại/phiên bản ảnh.
- Thời gian là UTC.
- Mỗi SELECT kiểm tra lại user active và quyền: khách theo `orders.customer_id` + user `active`/role `customer`; tiệm theo `orders.bakery_id` của route + membership `active` + user `active`/role `bakery`.
- Đơn của khách khác: 404 `ORDER_NOT_FOUND`; yêu cầu của khách khác: 404 `REQUEST_NOT_FOUND`. Không phân biệt "không tồn tại" với "không có quyền".
- Thành viên thuộc cả tiệm A và B không dùng route A để đọc hoặc quyết định đơn của B.
- Tiệm `suspended` hoặc hết hạn gói vẫn đọc được lịch sử nếu user và membership còn active. Khách chưa xác minh email vẫn đọc được đơn của mình.
- GET không ghi dữ liệu.

## Ghi qua procedure

- Mỗi thao tác mở **một connection riêng**, không có transaction ứng dụng, không retry. Procedure tự `START TRANSACTION`/`COMMIT`.
- Trên cùng connection, **trước CALL**:
  1. Kiểm tra `schema_migrations` có version 6. Không có thì trả 503 `ORDER_WORKFLOW_NOT_READY`, không gọi procedure cũ. Kiểm tra lúc thao tác, không lúc startup/health; không có tự động migrate.
  2. Accept: yêu cầu thuộc khách active và báo giá thuộc đúng yêu cầu trên route. Decision: actor là thành viên active của tiệm trên route và đơn thuộc đúng tiệm đó.

  Hai procedure suy ra yêu cầu/tiệm từ báo giá/đơn, nên kiểm tra route là bắt buộc. Procedure kiểm tra lại quyền dưới khóa; kiểm tra trước CALL **không** phải bảo đảm duy nhất về thời gian.
- OUT `p_order_id` đọc kiểu `Int64`; NULL hoặc kiểu khác trả 500, không đoán id. Sau CALL, service SELECT lại đơn theo đúng phạm vi actor; không đọc lại được thì 500.
- Lỗi nghiệp vụ chỉ được ánh xạ khi `SqlState = 45000` và `Message` khớp đúng allowlist của chính procedure đó, gồm các guard nó gọi (`OrderProcedureErrors`). Kiểm thử đối chiếu allowlist với bản có hiệu lực: accept/decide theo V006, `sp_guard_bakery_entitled` theo V005, guard khác theo install/02. Mã khác, lỗi CHECK/UNIQUE, deadlock, dữ liệu quá dài… trả 500 đã che chi tiết.
- **Mất kết nối sau khi gửi CALL**: procedure có thể đã commit. Server không retry; client không gửi lại mù mà gọi `GET /api/design-requests/{requestId}/order` (khách) hoặc `GET .../orders/{orderId}` (tiệm) để đối chiếu.

### Bảng lỗi

| Trường hợp | HTTP / code |
| --- | --- |
| Chưa đăng nhập / phiên bị thu hồi | 401 |
| Sai role | 403 |
| Accept khi email chưa xác minh | 403 `EMAIL_NOT_VERIFIED` |
| Thiếu/sai CSRF | 400 |
| Database chưa có V006 | 503 `ORDER_WORKFLOW_NOT_READY` |
| Yêu cầu không thuộc khách (`NOT_REQUEST_OWNER`, `USER_NOT_FOUND`, `USER_NOT_ACTIVE`, `USER_NOT_CUSTOMER`) | 404 `REQUEST_NOT_FOUND` |
| Báo giá không thuộc yêu cầu (`QUOTE_NOT_FOUND`) | 404 `QUOTE_NOT_FOUND` |
| Không phải thành viên active (`NOT_BAKERY_MEMBER`, `USER_NOT_*`, `BAKERY_NOT_FOUND` khi decide) | 404 `BAKERY_NOT_FOUND` |
| Đơn không thuộc tiệm/khách (`ORDER_NOT_FOUND`) | 404 `ORDER_NOT_FOUND` |
| Body/tham số sai; `INVALID_ARGUMENT`, `INVALID_DECISION` | 400 ValidationProblemDetails / 400 `INVALID_ARGUMENT` |
| `ORDER_REJECTION_REASON_REQUIRED` (procedure) | 400 cùng mã |
| `REQUEST_NOT_OPEN` (đã có đơn, đã hủy, chọn lại), `QUOTE_NOT_AVAILABLE` (superseded/withdrawn/accepted/not_selected), `QUOTE_EXPIRED`, `QUOTE_PICKUP_PASSED` | 409 cùng mã |
| `DESIGN_ASSET_*` khi accept (ảnh của báo giá không còn hợp lệ) | 409 `DESIGN_ASSET_INVALID` |
| `BAKERY_NOT_ACTIVE` (accept gồm cả `BAKERY_NOT_FOUND`), `BAKERY_SUBSCRIPTION_INACTIVE` (chỉ accept) | 409 cùng mã |
| `ORDER_NOT_PENDING`, `ORDER_PICKUP_PASSED` | 409 cùng mã |
| Lỗi hệ thống/database ngoài dự kiến | 500 được che chi tiết |

## Migration V006 (`database/migrations/V006__order_decision_time_rules.sql`)

Chỉ tạo lại `sp_accept_quote` (bản V005) và `sp_bakery_decide_order` (bản install/02). Không đổi bảng, cột, dữ liệu hay quyền. Không có file `.grants.sql`: procedure là SQL SECURITY DEFINER và backend đã có `EXECUTE ON decakes_db.*`.

- **Accept**: giữ nguyên thứ tự khóa (design_requests → quotes → khóa chia sẻ users/bakeries/media_assets → kỳ sử dụng), các guard, kiểm tra gói, câu INSERT bản chốt và transaction.
  - V005 lấy `v_now` **trước** `sp_guard_bakery_entitled`. Guard đọc `FOR SHARE` nên có thể phải chờ khóa, và khi đó hạn báo giá bị so với giờ trước lúc chờ.
  - V006 lấy lại `v_now` **sau** guard, tức sau mọi lần đọc có khóa. Giá trị đó dùng cho `QUOTE_EXPIRED`, `QUOTE_PICKUP_PASSED` (mới) và `created_at` của đơn.
  - Quyền gói vẫn đánh giá tại `v_now` lần đầu (sau khóa nghiệp vụ) như V005.
- **Decide**:
  - reject có `p_reason` NULL hoặc rỗng sau `TRIM` trả `ORDER_REJECTION_REASON_REQUIRED`, kiểm tra trước khi mở transaction. `TRIM` của MySQL chỉ bỏ dấu cách; API đã trim mọi khoảng trắng Unicode.
  - confirm với `v_now >= agreed_pickup_at` trả `ORDER_PICKUP_PASSED`. `agreed_pickup_at` đọc trong câu `FOR UPDATE` của orders; `v_now` lấy sau khi giữ đủ khóa như cũ.
- Kiểm thử không cần database xác nhận mọi dòng mã của bản cũ vẫn còn, đúng thứ tự, và chỉ có đúng các dòng nêu trên được thêm.
- **Vận hành**:
  - Precheck yêu cầu đã có version 5 và chưa có version 6. Version 6 chỉ được INSERT ở câu cuối, sau khi các câu trước thành công.
  - `DROP/CREATE PROCEDURE` là DDL, tự commit ngầm từng câu nên migration **không atomic**. Nếu lỗi giữa chừng, procedure có thể đang thiếu và version 6 chưa được ghi; cần kiểm tra thủ công, không coi là có thể tự động chạy lại an toàn.
  - Từ lúc DROP cho tới khi ghi version 6, API vẫn trả 503 cho accept/decision. Một CALL đã qua bước kiểm tra version đúng lúc procedure bị DROP sẽ lỗi 500 đã che.
- Fixture kiểm thử nạp V003–V006 theo thứ tự. File grants vẫn suy ra từ tên migration, trừ migration nằm trong danh sách `SchemaScript.MigrationsWithoutGrants` (chỉ V006); không cấp thêm quyền nào.

## Kiểm chứng (02/11/2026)

- Application `OrderServiceTests` (33):
  - mọi trạng thái schema dùng được làm bộ lọc;
  - query sai không gọi store, mặc định phân trang;
  - forwarding actor/phạm vi/cancellation, đọc lại đúng phạm vi;
  - validation decision/reason: đúng 500 ký tự, 501, NUL, surrogate lẻ, confirm có lý do;
  - từ chối không đọc lại; thiếu id hoặc không đọc lại được không bị coi là thành công.
- API `OrderApiTests` (47, store giả):
  - 401/403 cho 5 GET và 2 POST; CSRF thiếu/sai; accept khi email chưa xác minh trả 403 nhưng GET vẫn đọc được;
  - actor từ phiên (bỏ qua `customerId`/`actorUserId` trong body); 201 + `Location`; đúng tập trường JSON;
  - phân trang/lọc, query sai 400; 404 có mã;
  - validation decision; ánh xạ 11 kết quả accept và 8 kết quả decision, gồm 503 `ORDER_WORKFLOW_NOT_READY` khi thiếu V006;
  - lỗi lạ 500 đã che; phiên bị vô hiệu không ghi/đọc được.
- Không cần database (`ScriptAndErrorMappingTests`, 19 ca mới):
  - V006 nạp được, không cần grants, version 6 là câu cuối, không có DDL bảng;
  - V006 chỉ thêm đúng các dòng đã duyệt;
  - allowlist từng procedure khớp mọi `MESSAGE_TEXT` của procedure và guard; mã ngoài allowlist/sai hoa thường/SQLSTATE khác không được ánh xạ.
- MySQL 8.4.13 disposable loopback, TLS 1.3 (`MySqlOrderStoreTests`, 14):
  - OUT `p_order_id` khớp dòng thật; bản chốt đúng mọi `agreed_*` (giá tối đa vượt ngân sách, giờ nhận khác yêu cầu, ảnh design_proposal v3, URL ảnh lưu trong DB nhưng không trả); sửa trực tiếp nguồn không đổi đơn;
  - chốt route/ownership trước CALL, khách khác không thấy đơn, khách bị khóa không đọc được;
  - 8 lần chọn đồng thời hai báo giá của một yêu cầu chỉ tạo 1 đơn, còn lại `REQUEST_NOT_OPEN`;
  - superseded/withdrawn bị chặn `QUOTE_NOT_AVAILABLE`, hết hạn bị chặn `QUOTE_EXPIRED`, báo giá cũ còn hạn nhưng đã qua giờ nhận bị chặn `QUOTE_PICKUP_PASSED`;
  - hết hạn trôi qua khi chờ khóa yêu cầu; giờ nhận trôi qua khi **guard gói chờ khóa kỳ sử dụng**; giờ nhận trôi qua khi confirm chờ khóa. Mỗi ca xác nhận thao tác thực sự ở trạng thái LOCK WAIT trước mốc thời gian;
  - confirm sau giờ nhận bị chặn, reject cần lý do (NULL và chỉ khoảng trắng bị procedure chặn) rồi thành công; yêu cầu `cancelled`, báo giá vẫn `accepted`, không có đơn thứ hai; quyết định lặp trả `ORDER_NOT_PENDING`;
  - 8 quyết định đồng thời confirm/reject chỉ 1 chuyển trạng thái;
  - owner/staff, membership removed, user disabled, một thành viên thuộc hai tiệm;
  - gói hết hạn chặn tạo đơn nhưng confirm đơn cũ được; tiệm suspended chặn accept/confirm, reject vẫn được; lịch sử vẫn đọc được;
  - thiếu version 6 chặn cả hai thao tác ghi trước CALL, đọc vẫn chạy;
  - lỗi ngoài allowlist (lý do > 500 ký tự, SQLSTATE khác 45000) không ghi gì;
  - lọc/phân trang/thứ tự khi `created_at` trùng, offset lớn, GET không đổi dòng nào.
- Chưa chạy đối chứng các ca chờ khóa với procedure V005 (chỉ suy luận rằng V005 sẽ cho qua).
- **Chưa kiểm chứng HTTPS end-to-end với store thật và MySQL** cho Orders: kiểm thử API dùng store giả, store được kiểm chứng riêng trên MySQL.

Xem thêm [báo giá](QUOTES.md), [nghiệp vụ](BUSINESS_RULES_REVIEW.md), [schema](../database/SCHEMA.md), [procedure](../database/STORED_PROCEDURES.md), [kiểm thử](../tests/README.md).
