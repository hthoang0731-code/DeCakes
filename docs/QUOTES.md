# Báo giá — phạm vi và hợp đồng hiện tại

Ngày 01/11/2026, nhánh `feature/quotes`. Đây là phần triển khai tiếp sau nghiệm thu onboarding/admin. Không có thay đổi schema, quyền hoặc dữ liệu Aiven trong task này.

## Đã triển khai: đọc báo giá

| Endpoint | Quyền | Kết quả |
| --- | --- | --- |
| `GET /api/design-requests/{requestId}/quotes` | Customer sở hữu yêu cầu | Trang lịch sử báo giá của các tiệm đã gửi |
| `GET /api/design-requests/{requestId}/quotes/{quoteId}` | Customer sở hữu yêu cầu | Một phiên bản thuộc đúng yêu cầu |
| `GET /api/bakery-workspace/bakeries/{bakeryId}/requests/{requestId}/quotes` | Thành viên active của đúng tiệm | Trang lịch sử báo giá của riêng tiệm cho yêu cầu đã nhận |
| `GET /api/bakery-workspace/bakeries/{bakeryId}/requests/{requestId}/quotes/{quoteId}` | Thành viên active của đúng tiệm | Một phiên bản thuộc đúng tiệm và yêu cầu |

Danh sách nhận `status`, `page`, `pageSize`: mặc định trang 1/cỡ 20, tối đa 100. Trạng thái cho phép là submitted, superseded, withdrawn, accepted, not_selected. Không có trạng thái lưu `expired`. Tham số sai trả 400 trước khi truy vấn. Kết quả `{ items, page, pageSize, totalCount }`, sắp xếp created_at DESC, id DESC; không truyền status thì trả mọi phiên bản, kể cả hết hạn và đã rút.

Mỗi phiên bản gồm id, requestId, bakeryId, bakeryName, revisionNo, status, price, currency, sizeDescription, servings, flavor, decorationDescription, inscriptionText, note, estimatedPickupAt, validUntil, designAssetId, withdrawnAt, createdAt, updatedAt, isCurrent, isExpired. Không trả id người tạo, thông tin tài khoản khách hoặc URL ảnh riêng tư.

- `isCurrent`: status submitted hoặc accepted, khớp current_slot trong schema.
- `isExpired`: validUntil <= thời điểm đánh giá của service qua TimeProvider, một thời điểm chung cho toàn bộ trang. Không tự ghi trạng thái hoặc cập nhật database khi đọc.
- Hai cờ này **không xác nhận một báo giá được phép chọn tạo đơn**. Ví dụ accepted vẫn current nhưng không thể chọn lần nữa; báo giá submitted chưa hết hạn vẫn cần kiểm tra hồ sơ, gói và trạng thái yêu cầu khi thực sự chọn.
- Thời gian trả UTC. Tiệm suspended/hết hạn gói vẫn được đọc lịch sử; user/membership phải còn active.
- Chưa xác minh email vẫn được đọc lịch sử của mình, theo chính sách Accounts hiện tại. Admin không tự có quyền customer/bakery.

## Cách ly dữ liệu và lỗi

Actor lấy từ claim đã được kiểm tra phiên, không lấy từ body/query. Store kiểm tra lại chủ yêu cầu hoặc membership/user active trong từng câu SELECT; tham số route chỉ là điều kiện tra cứu, không chứng minh quyền.

| Trường hợp | HTTP/code |
| --- | --- |
| Chưa đăng nhập | 401 |
| Sai role | 403 |
| Tiệm không tồn tại hoặc không phải thành viên active | 404 BAKERY_NOT_FOUND |
| Yêu cầu không tồn tại, không thuộc khách hoặc chưa gửi đến tiệm | 404 REQUEST_NOT_FOUND |
| Báo giá không thuộc đúng tiệm/yêu cầu hoặc không tồn tại | 404 QUOTE_NOT_FOUND |
| Tham số danh sách sai | 400 ValidationProblemDetails |
| Lỗi hệ thống/database ngoài dự kiến | 500 được che chi tiết |

Thành viên thuộc hai tiệm vẫn không được dùng route tiệm A để đọc báo giá của tiệm B. Customer khác không biết yêu cầu/báo giá có tồn tại hay không qua các endpoint này. GET không ghi viewed, status hoặc thời gian.

## Đã triển khai: ghi báo giá của tiệm

Chọn báo giá tạo đơn thuộc Orders, xem [ORDERS](ORDERS.md) (02/11/2026). Phần ghi báo giá không sửa SQL nguồn, không chạy migration.

| Endpoint | Procedure | Thành công |
| --- | --- | --- |
| `POST /api/bakery-workspace/bakeries/{bakeryId}/requests/{requestId}/quotes` | `sp_create_quote` | 201, `Location` tới `.../quotes/{id}`, body là phiên bản vừa tạo |
| `POST .../quotes/{quoteId}/revisions` | `sp_revise_quote` | 201, `Location` tới phiên bản **mới** (id mới); phiên bản cũ thành `superseded` |
| `POST .../quotes/{quoteId}/withdraw` | `sp_withdraw_quote` | 200, body là phiên bản đã rút (`withdrawn`, `withdrawnAt`) |

Body tạo/sửa (sửa gửi toàn bộ nội dung mới): `{ price, sizeDescription?, servings?, flavor, decorationDescription, inscriptionText?, note?, estimatedPickupAt, validUntil, designAssetId? }`. Tiền tệ luôn VND, không nhận từ client.

Policy `Bakery`, cookie phiên đã kiểm tra lại mỗi request, CSRF bắt buộc (filter toàn cục). Actor lấy từ claim. Thao tác ghi của tiệm **chưa** yêu cầu email đã xác minh: quy tắc này chưa được chốt.

### Quy tắc đã duyệt (01/11/2026)

1. Tiệm **được báo giá cao hơn ngân sách và đề xuất giờ nhận khác yêu cầu**. API không so sánh hay ép giá/giờ theo yêu cầu ban đầu. Nội dung yêu cầu không bị ghi đè.
2. Khi tạo/sửa, API **bắt buộc `now < validUntil <= estimatedPickupAt`**. `now` lấy từ `TimeProvider`. Thời gian phải là ISO 8601 có Z/offset, được đổi sang UTC, chỉ chính xác tới mili giây (DATETIME(3)); sai thì bị từ chối, không tự sửa hoặc làm tròn. Procedure kiểm tra lại `validUntil > now` và `estimatedPickupAt > now` bằng giờ database sau khi giữ khóa (`QUOTE_INVALID_TIMES`).

Kiểm tra lại giờ nhận khi khách chọn báo giá thuộc phần Orders: migration đề xuất V006 kiểm tra trong `sp_accept_quote` sau khi giữ khóa (chỉ kiểm thử local, xem [ORDERS](ORDERS.md)); validation trước CALL không thay thế kiểm tra đó.

### Validation theo schema (400 ValidationProblemDetails, không gọi store)

- `price`: bắt buộc, > 0, ≤ 9 999 999 999,99, tối đa 2 chữ số thập phân (DECIMAL(12,2)); không làm tròn. Số sai kiểu hoặc vượt `decimal` bị model binding trả 400.
- VARCHAR đếm theo **ký tự Unicode** như utf8mb4: `sizeDescription` 100, `flavor` 200 (bắt buộc), `inscriptionText` 200; một dòng, không ký tự điều khiển.
- TEXT đếm theo **byte UTF-8**, tối đa 65 535: `decorationDescription` (bắt buộc), `note`; cho phép xuống dòng/tab.
- Chuỗi được trim và chuẩn hóa NFC; chuỗi rỗng thành null. UTF-16 lỗi (surrogate lẻ) bị từ chối vì driver sẽ âm thầm thay bằng U+FFFD.
- Cần `sizeDescription` hoặc `servings`; `servings` 1..65 535 (SMALLINT UNSIGNED); `designAssetId` > 0 nếu có.

### Cách ly và gọi procedure

- Mỗi thao tác mở một connection riêng, không có transaction ứng dụng, không retry. Nếu kết nối đứt khi CALL, không biết procedure đã commit hay chưa; client đọc lại lịch sử.
- **Trước CALL**, store kiểm tra trên cùng connection:
  - actor là user `active`, role `bakery` và thành viên `active` (owner/staff) của tiệm trên route;
  - yêu cầu đã gửi đến tiệm đó;
  - với sửa/rút, báo giá thuộc **đúng** cặp `bakeryId`/`requestId` trên route.

  Lý do: revise/withdraw suy ra tiệm từ báo giá, nên một user thuộc cả tiệm A và B có thể thao tác báo giá của B qua route của A nếu không chặn. Procedure kiểm tra lại quyền thành viên dưới khóa.
- Gọi bằng `CommandType.StoredProcedure`. OUT `p_quote_id`/`p_new_quote_id` đọc kiểu `Int64`; NULL hoặc kiểu khác ném lỗi (500), không đoán id. Sau CALL, service đọc lại phiên bản trong cùng phạm vi tiệm/yêu cầu của actor.
- Lỗi nghiệp vụ chỉ được ánh xạ khi `SqlState = 45000` và `Message` khớp đúng allowlist **của chính procedure đó**, gồm các guard nó gọi (`QuoteProcedureErrors`). Kiểm thử đối chiếu allowlist với bản có hiệu lực: create/revise theo V005, withdraw và các guard theo install/02. Mã của procedure khác, lỗi CHECK/UNIQUE, deadlock, dữ liệu quá dài… trả 500 đã che chi tiết.

| Kết quả | HTTP / code |
| --- | --- |
| Không phải thành viên active, tiệm không tồn tại (`NOT_BAKERY_MEMBER`, `USER_NOT_*`, `BAKERY_NOT_FOUND`) | 404 `BAKERY_NOT_FOUND` |
| Yêu cầu không tồn tại/chưa gửi đến tiệm (`REQUEST_NOT_FOUND`, `REQUEST_NOT_SENT_TO_BAKERY`) | 404 `REQUEST_NOT_FOUND` |
| Báo giá không thuộc đúng tiệm/yêu cầu (`QUOTE_NOT_FOUND`) | 404 `QUOTE_NOT_FOUND` |
| `INVALID_ARGUMENT`, `QUOTE_INVALID_PRICE`, `QUOTE_INVALID_TIMES` | 400 cùng mã |
| `DESIGN_ASSET_NOT_FOUND`, `DESIGN_ASSET_WRONG_REQUEST`, `DESIGN_ASSET_WRONG_BAKERY` | 400 `DESIGN_ASSET_INVALID` |
| `REQUEST_NOT_OPEN`, `REQUEST_DECLINED_BY_BAKERY`, `QUOTE_ALREADY_EXISTS`, `QUOTE_ALREADY_ACCEPTED`, `QUOTE_NOT_CURRENT`, `BAKERY_NOT_ACTIVE`, `BAKERY_SUBSCRIPTION_INACTIVE` | 409 cùng mã |

### Quy tắc SQL được giữ nguyên

- Mỗi phiên bản bất biến; sửa tạo id mới với `revision_no + 1` và chỉ đổi `status`/`updated_at` của phiên bản cũ. Sửa bằng id cũ trả `QUOTE_NOT_CURRENT`.
- Báo giá `submitted` đã hết hạn vẫn sửa được nếu nội dung mới hợp lệ.
- Rút không đổi `request_bakeries` khỏi `quoted`. Rút lần hai trả `QUOTE_NOT_CURRENT`. Sau khi rút, tạo báo giá mới được (revision tiếp theo).
- Tạo/sửa cần gói còn hạn và tiệm `active`. Rút vẫn được khi gói hết hạn hoặc tiệm `suspended`.
- Đồng thời: nhiều lần tạo cùng lúc cho một cặp yêu cầu–tiệm chỉ một thành công, còn lại `QUOTE_ALREADY_EXISTS`. Nhiều lần sửa cùng một phiên bản chỉ tạo đúng một phiên bản mới, còn lại `QUOTE_NOT_CURRENT`.

## Kiểm chứng phần ghi (01/11/2026)

- Application `QuoteWriteServiceTests` (35): validator theo từng trường và biên DECIMAL/VARCHAR/TEXT/UTF-16/thời gian; không gọi store khi sai; forwarding actor/phạm vi/cancellation; đọc lại; từ chối không đọc lại; thiếu id hoặc không đọc lại được không bị coi là thành công.
- API `QuoteWriteApiTests` (27): 401, CSRF thiếu/sai, 403 customer/admin, 201 + `Location`, 200 khi rút, ValidationProblemDetails, JSON sai kiểu, ánh xạ 14 kết quả, lỗi lạ 500 đã che, phiên bị vô hiệu không ghi được.
- Không cần database (`ScriptAndErrorMappingTests`, 12 ca mới): allowlist từng procedure khớp đúng mọi `MESSAGE_TEXT` của procedure và guard nó gọi; mã của procedure khác/sai hoa thường/SQLSTATE khác không được ánh xạ.
- MySQL 8.4.13 disposable loopback (`MySqlQuoteWriteStoreTests`, 11):
  - OUT và giá trị chính xác: giá tối đa, 200 emoji, TEXT 65 535 byte, UTC mili giây;
  - giá vượt ngân sách, giờ nhận khác yêu cầu;
  - chốt route với user thuộc hai tiệm; staff, membership bị gỡ, user bị khóa, sai role;
  - lịch sử bất biến; sửa báo giá hết hạn; rút giữ `quoted`; gói hết hạn; declined/cancelled/accepted/suspended;
  - ảnh thiết kế; guard thời gian/giá của procedure; lỗi ngoài allowlist không ghi gì;
  - tạo/sửa đồng thời.

## Kiểm chứng phần đọc

**Bổ sung nghiệm thu HTTP thật ngày 01/11/2026:** 41 request API qua Kestrel HTTPS với service/store thật và MySQL 8.4.13 disposable, 57 kiểm tra PASS/0 FAIL. Luồng gồm đăng ký/xác minh bằng hộp thư giả, admin duyệt/trial, khách gửi yêu cầu, tiệm tạo/sửa/rút/tạo lại, Location, lịch sử bất biến, giá vượt ngân sách/giờ nhận khác, CSRF thiếu, sai role và thao tác lặp. Metadata cuối: 3 phiên bản (1 superseded, 1 withdrawn, 1 submitted), giá bản đầu giữ nguyên, ngân sách yêu cầu không đổi, request_bakeries vẫn quoted, 0 order. Email giả chỉ phục vụ kiểm thử, không gọi Resend/payOS hoặc ghi Aiven. Đã dọn host/MySQL/database/user/file mật khẩu tạm. API cổng 7215 đã build và mở lại, OpenAPI có đủ 3 POST mới. Báo cáo chi tiết cục bộ: `TestResults/quotes-http-review-20261101/HTTP_REVIEW.md` (Git bỏ qua).

- Application: 14 kiểm thử đọc, filter/paging, ranh giới hết hạn, một thời điểm cho cả trang, forwarding scope/cancellation và lỗi hệ thống.
- API fake/in-memory: 23 kiểm thử 401/403/404/400, actor từ phiên, đúng trường JSON, đọc khi email chưa xác minh, phân trang và lỗi được che.
- MySQL 8.4.13 disposable: 7 kiểm thử read mapping, Unicode/null/UTC, cách ly khách/tiệm, một user thuộc hai tiệm, removed/disabled/sai role, đọc lịch sử khi suspended/hết hạn, lọc/thứ tự/offset 64 bit và GET không ghi.
- Setup các ca MySQL gọi procedure tạo/sửa/rút nguồn qua đúng quyền backend. Đã đọc được OUT p_quote_id và p_new_quote_id khi CommandType.StoredProcedure, không transaction ứng dụng. Đây là kiểm chứng driver và setup ở checkpoint phần đọc; store/API ghi được kiểm chứng riêng ở mục trên.
- Fixture sau kiểm thử: 0 database/tài khoản tạm; MySQL disposable và thư mục dữ liệu tạm đã dọn. Không ghi Aiven, không gửi email thật, không commit/push.

Xem nguồn tham chiếu [nghiệp vụ](BUSINESS_RULES_REVIEW.md), [schema](../database/SCHEMA.md), [procedure](../database/STORED_PROCEDURES.md) và [hướng dẫn kiểm thử](../tests/README.md).
