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

## Quy tắc đã chốt cho phần ghi tiếp theo

Tạo/sửa/rút báo giá và chọn báo giá tạo đơn **chưa có endpoint mới trong lần triển khai phần đọc này**. SQL nguồn đã có procedure; không thay SQL để hoàn thiện API khi chưa được duyệt riêng.

Người dùng đã đồng ý cả hai quyết định ngày 01/11/2026:

1. Tiệm **được báo giá cao hơn ngân sách và đề xuất giờ nhận khác yêu cầu**. Giá và giờ thực tế phải được thể hiện rõ để khách xem trước khi chọn; ngân sách/giờ mong muốn không tự ghi đè giá trị đã báo.
2. Khi tạo/sửa báo giá, API **bắt buộc `now < validUntil <= estimatedPickupAt`**. Sai quan hệ bị từ chối, không tự sửa giờ hoặc làm tròn dữ liệu.

Các quyết định trên là yêu cầu cho phần ghi sẽ triển khai tiếp; chưa có validator/endpoint ghi ở checkpoint này. Việc kiểm tra lại giờ nhận khi khách chọn báo giá cần được xử lý riêng ở tầng procedure/Orders sau khi giữ khóa; validation trước CALL không thay thế kiểm tra đó. Không tự sửa SQL nguồn hoặc áp dụng migration lên Aiven.

Các quy tắc SQL hiện có cần giữ khi triển khai ghi: mỗi phiên bản bất biến; revise tạo id mới; báo giá submitted đã hết hạn vẫn được revise nếu nội dung mới hợp lệ; withdraw không đổi request_bakeries khỏi quoted; tạo/sửa cần gói còn hạn, rút vẫn được khi gói hết hạn. Procedure tự quản lý transaction, gọi bằng connection riêng không có transaction ứng dụng và không retry mù.

## Kiểm chứng mới

- Application: 14 kiểm thử đọc, filter/paging, ranh giới hết hạn, một thời điểm cho cả trang, forwarding scope/cancellation và lỗi hệ thống.
- API fake/in-memory: 23 kiểm thử 401/403/404/400, actor từ phiên, đúng trường JSON, đọc khi email chưa xác minh, phân trang và lỗi được che.
- MySQL 8.4.13 disposable: 7 kiểm thử read mapping, Unicode/null/UTC, cách ly khách/tiệm, một user thuộc hai tiệm, removed/disabled/sai role, đọc lịch sử khi suspended/hết hạn, lọc/thứ tự/offset 64 bit và GET không ghi.
- Setup các ca MySQL gọi procedure tạo/sửa/rút nguồn qua đúng quyền backend. Đã đọc được OUT p_quote_id và p_new_quote_id khi CommandType.StoredProcedure, không transaction ứng dụng. Đây là kiểm chứng driver và setup, **chưa phải triển khai store/API ghi**.
- Fixture sau kiểm thử: 0 database/tài khoản tạm; MySQL disposable và thư mục dữ liệu tạm đã dọn. Không ghi Aiven, không gửi email thật, không commit/push.

Xem nguồn tham chiếu [nghiệp vụ](BUSINESS_RULES_REVIEW.md), [schema](../database/SCHEMA.md), [procedure](../database/STORED_PROCEDURES.md) và [hướng dẫn kiểm thử](../tests/README.md).
