# Kế hoạch backend làm việc với database

Tài liệu kế hoạch, chưa có mã nào được triển khai. Căn cứ: [database/SCHEMA.md](../database/SCHEMA.md), [database/STORED_PROCEDURES.md](../database/STORED_PROCEDURES.md) và các file SQL trong [database/](../database/). Mục đánh dấu **[cần chốt]** là quyết định nhóm phải đưa ra trước khi viết mã; **[cần xác minh]** là điều chưa kiểm chứng được từ SQL và phải thử trên môi trường thật.

## 1. Đối chiếu SQL với quy tắc nghiệp vụ

| Quy tắc | SQL hiện có | Đánh giá |
| --- | --- | --- |
| Khách xem mẫu bánh hoặc tạo yêu cầu riêng (ý tưởng, ảnh tham khảo, ngân sách, kích thước, hương vị, ngày nhận) | `cakes` (catalog, `visibility`), `design_requests` (`idea_description`, `budget_min/max`, `size_description`, `servings`, `flavor`, `inscription_text`, `pickup_at`, `inspired_by_cake_id`), ảnh tham khảo là `media_assets` loại `customer_reference` | Khớp. Không có procedure; ghi trực tiếp bảng |
| Một yêu cầu gửi đến nhiều tiệm | `request_bakeries` + `sp_send_request_to_bakery` (mỗi lần một tiệm) | Khớp |
| Tiệm gửi báo giá, có phiên bản sửa đổi | `quotes` (mỗi dòng một phiên bản), `sp_create_quote`, `sp_revise_quote`, `sp_withdraw_quote` | Khớp |
| Khách chọn một báo giá để tạo đơn | `sp_accept_quote` | Khớp |
| Tiệm xác nhận hoặc từ chối đơn | `sp_bakery_decide_order` | Khớp |
| Một yêu cầu tạo tối đa một đơn; tiệm từ chối thì khách tạo yêu cầu mới | `uq_orders_design_request`; từ chối đặt yêu cầu `cancelled` | Khớp |
| Tiệm phải xác nhận khả năng làm bánh | Chỉ có hành động `confirm` của đơn sau khi khách đã chọn; không có cột hay bước riêng cho việc tiệm cam kết làm được | **Khác/thiếu [cần chốt]**: hiểu "xác nhận khả năng" là bước `confirm` hiện có, hay cần bước khác (ví dụ khi báo giá)? |
| AI tạo ảnh (tùy chọn, sau) | `media_assets.asset_kind = 'ai_generated'` đã hỗ trợ; không có bảng job/prompt | Đủ chỗ lưu ảnh; phần còn lại ngoài schema |

Các khác biệt khác, đều nằm trong SQL hiện có:

- **Tiệm từ chối yêu cầu** (`request_bakeries.status = 'declined'` và `decline_reason` đã có trong schema) nhưng **không có procedure**. `sp_create_quote` đã kiểm tra `REQUEST_DECLINED_BY_BAKERY`, nên trạng thái này được kỳ vọng tồn tại. Backend phải UPDATE trực tiếp (script quyền cho phép cột `status, decline_reason, responded_at, updated_at`). **[cần chốt]** có cần procedure riêng không.
- **Khách hủy yêu cầu hoặc hủy đơn, đơn → `ready`/`picked_up`**: không có procedure (nguồn thiết kế ghi rõ là chưa làm). Backend cập nhật trực tiếp cột trạng thái.
- **`sp_withdraw_quote`** không đổi `request_bakeries.status`, nên sau khi rút, trạng thái tiệm vẫn là `quoted`. **[cần chốt]** có chấp nhận hay không.
- **Báo giá đã hết hạn vẫn `submitted`** (hết hạn không phải trạng thái lưu); danh sách báo giá phải tự lọc theo `valid_until`.
- **Sau khi tiệm từ chối đơn**, báo giá liên quan vẫn ở `accepted`.
- Không có lịch sử trạng thái, thông báo, thanh toán, chat, giao hàng (đúng phạm vi đã nêu).

## 2. Hướng truy cập dữ liệu (đã chốt)

Quyết định kỹ thuật hiện tại, thay thế phương án EF Core/scaffold ban đầu của tài liệu này:

- **ADO.NET với MySqlConnector 2.6.2**, kết nối `SslMode=VerifyFull` với CA Aiven, tài khoản `decakes_app`. Đã kiểm chứng `VerifyFull` chạy chỉ với `SslCa`.
- **Chưa dùng EF Core**: không có DbContext, không scaffold, không model persistence do công cụ sinh. Provider Oracle `MySql.EntityFrameworkCore` đòi chứng chỉ client với `VerifyFull`, còn Pomelo chưa có bản stable cho EF Core 10. Không kết hợp Oracle EF (VerifyCA) với MySqlConnector (VerifyFull).
- Giữ .NET 10, Controllers và 4 project Clean Architecture.

Vị trí trong Infrastructure:

```
src/DeCakes.Infrastructure/
  Persistence/
    IMySqlConnectionFactory.cs   Mở một connection cho mỗi thao tác (async, CancellationToken)
    MySqlConnectionFactory.cs    Đọc ConnectionStrings:DeCakesDb, kiểm tra VerifyFull + CA, không log cấu hình
    <theo chức năng>/            Triển khai truy vấn và gọi procedure, ánh xạ sang DTO của Application
```

Nguyên tắc:

- Application định nghĩa DTO và interface theo chức năng (Accounts, DesignRequests, Quotes, Orders...). Infrastructure triển khai và ánh xạ. Kiểu `MySqlConnector` không xuất hiện trong Application hoặc Domain.
- Domain chỉ chứa khái niệm và quy tắc cần độc lập với hạ tầng (vòng đời trạng thái, định nghĩa báo giá hết hạn, UTC); không phản chiếu từng bảng.
- Factory không giữ connection mở và không kết nối khi khởi động; chuỗi cấu hình sai bị từ chối ở lần mở đầu tiên với thông báo không chứa giá trị cấu hình. Connection pooling do MySqlConnector đảm nhiệm.
- `DateTimeKind=Utc` được đặt trong chuỗi kết nối: dữ liệu `DATETIME(3)` đọc ra có `Kind = Utc`; luôn ghi giờ UTC.
- Không Generic Repository, không Unit of Work.
- Cột `quotes.current_slot` là cột generated: không bao giờ đưa vào INSERT/UPDATE.
- Mọi SQL dùng parameter, không nối dữ liệu người dùng vào câu lệnh.

## 3. Phân loại thao tác

| Loại | Cách thực hiện | Ví dụ |
| --- | --- | --- |
| Đọc (danh sách, chi tiết) | `SELECT` có parameter qua ADO.NET, ánh xạ sang DTO | Danh sách mẫu bánh, yêu cầu của khách, báo giá của yêu cầu, đơn |
| Ghi không có procedure | `INSERT/UPDATE` có parameter; ghi nhiều bảng có thể dùng transaction riêng trong Infrastructure | Tạo/sửa `design_requests`, thêm `media_assets`, đăng ký `users`, tạo tiệm + thành viên, tiệm từ chối yêu cầu, đơn → `ready`/`picked_up`/`cancelled` |
| Ghi qua procedure tự quản lý transaction | `CALL` trên connection **không có transaction ứng dụng đang mở** | 6 procedure nghiệp vụ: `sp_send_request_to_bakery`, `sp_create_quote`, `sp_revise_quote`, `sp_withdraw_quote`, `sp_accept_quote`, `sp_bakery_decide_order` |
| Procedure guard | Không gọi từ backend | 5 procedure `sp_guard_*` |

Chỉ 6 procedure nghiệp vụ tự `START TRANSACTION`/`COMMIT` mới bắt buộc gọi ngoài transaction ứng dụng, vì `START TRANSACTION` trong procedure ngầm commit transaction đang mở. Các thao tác ghi khác không bị ràng buộc này.

## 4. Cách gọi procedure

1. Application định nghĩa interface theo chức năng với tham số và DTO của Application.
2. Infrastructure mở connection từ `IMySqlConnectionFactory`, tạo `MySqlCommand` với `CommandType.StoredProcedure`, thêm parameter đúng thứ tự và kiểu theo [STORED_PROCEDURES.md](../database/STORED_PROCEDURES.md) (`sp_create_quote` có 14 tham số, `sp_revise_quote` có 13), OUT parameter khai báo bằng `ParameterDirection.Output`. Cách đọc OUT với MySqlConnector **[cần xác minh bằng thử nghiệm]**.
3. Procedure không trả result set; sau khi gọi, đọc kết quả bằng SELECT có parameter.
4. Bắt `MySqlException` với `SqlState == "45000"` và lấy mã lỗi từ `Message` để chuyển thành lỗi nghiệp vụ của Application. Lỗi khác SQLSTATE là lỗi hệ thống, không hiển thị nguyên văn cho người dùng. **[cần xác minh trên MySqlConnector: SqlState, Number, Message]**
5. Api ánh xạ mã lỗi sang HTTP và `ProblemDetails`. Bảng ánh xạ **[cần chốt]**: ví dụ `*_NOT_FOUND` → 404, `NOT_REQUEST_OWNER`/`NOT_BAKERY_MEMBER` → 403, `QUOTE_EXPIRED`/`REQUEST_NOT_OPEN`/`QUOTE_NOT_AVAILABLE`/`QUOTE_ALREADY_EXISTS` → 409, `INVALID_ARGUMENT`/`QUOTE_INVALID_*` → 400 hoặc 422.
6. Id người thực hiện lấy từ claim đã xác thực, không từ body.
7. Retry chỉ khi gặp deadlock hoặc lock wait timeout; không retry mù khi có lỗi nghiệp vụ.

`database/STORED_PROCEDURES.md` đã được cập nhật theo quy ước ADO.NET/MySqlConnector ở mục "Chung: lưu ý khi gọi từ backend"; phần SQL nguồn không đổi.
## 5. SQL hỗ trợ Accounts đến đâu

Có:

- Bảng `users`: `email` (UNIQUE; collation `ai_ci` nên không phân biệt hoa thường và dấu), `password_hash` (VARCHAR(255), chỉ lưu hash), `full_name`, `phone`, `system_role` (`customer`/`bakery`/`admin`), `status` (`active`/`disabled`).
- Bảng `bakery_members` liên kết user-tiệm, `member_role` `owner`/`staff`.
- Script quyền cho phép backend `INSERT/UPDATE` trực tiếp `users`, `bakeries`, `bakery_members`.
- Các procedure nghiệp vụ kiểm tra `users.status`, `system_role` và thành viên tiệm.
- Dữ liệu demo có `password_hash = NULL`, nghĩa là các tài khoản demo **không đăng nhập được**.

Thiếu (không tự tạo thêm):

- **Không có procedure đăng ký hay đăng nhập.** Đăng ký là INSERT trực tiếp vào `users`; đăng nhập là SELECT theo email và so khớp hash ở backend. Điều này phù hợp với thiết kế nguồn: "stored procedure không xác thực người dùng".
- Không có bảng cho refresh token, xác minh email, đặt lại mật khẩu, khóa tài khoản tạm thời sau nhiều lần sai, hay nhật ký đăng nhập.
- Không có luồng tạo tiệm cùng thành viên đầu tiên trong một thao tác; cần backend tự ghép (`bakeries` + `bakery_members`) và nên bọc trong transaction EF vì không dính procedure.
- Không có quản lý vai trò `admin` trong procedure nào; `admin` chỉ tồn tại như giá trị trong CHECK.
- Mỗi người chỉ có một `system_role`: một user vừa là khách vừa là nhân viên tiệm không được (guard khách đòi `customer`, guard thành viên đòi `bakery`). **[cần chốt]** phù hợp với nhu cầu hay không.

## 6. Thứ tự triển khai

1. **Accounts**: đăng ký, đăng nhập, xác thực, claim (user id, role). Đồng thời tạo tiệm và thành viên (cần cho báo giá). Nền cho mọi bước sau.
2. **Catalog đọc** (Bakeries/mẫu bánh, chỉ đọc) có thể làm song song, chia việc cho thành viên thứ hai.
3. **DesignRequests**: tạo/sửa yêu cầu, upload ảnh tham khảo (ghi `media_assets`), gửi đến tiệm (`sp_send_request_to_bakery`). Tiệm xem yêu cầu nhận được.
4. **Quotes**: tạo, sửa, rút báo giá (3 procedure); khách xem báo giá của yêu cầu, lọc hết hạn.
5. **Orders**: chọn báo giá (`sp_accept_quote`), tiệm xác nhận/từ chối (`sp_bakery_decide_order`), sau đó các trạng thái `ready`/`picked_up`/`cancelled` bằng cập nhật trực tiếp.
6. AI tạo ảnh để sau cùng.

Lý do: Quotes cần thành viên tiệm và yêu cầu đã gửi; Orders cần báo giá đã tạo.

## 7. Quyết định còn cần chốt

| # | Quyết định | Ảnh hưởng |
| --- | --- | --- |
| 1 | Cơ chế đăng nhập: ASP.NET Core Identity hay tự xây trên `users` (`PasswordHasher`); cookie hay JWT; có refresh token không. Identity cần bảng riêng, không khớp `users` hiện có nếu giữ nguyên schema | Authentication, có thể cần thay đổi schema |
| 2 | Tiệm từ chối yêu cầu: UPDATE trực tiếp hay thêm procedure; có cần "đã xem" | Thiếu procedure |
| 3 | "Tiệm xác nhận khả năng làm bánh" nghĩa là gì (mục 1) | Có thể cần thay đổi nghiệp vụ |
| 4 | Một user chỉ một vai trò; khách và tiệm có thể là cùng người không | Accounts |
| 5 | Bảng ánh xạ mã lỗi procedure sang HTTP và thông điệp | Api |
| 6 | Quy ước tạo tài khoản demo có mật khẩu để thử đăng nhập (dữ liệu demo hiện có `password_hash` NULL) | Môi trường phát triển |
| 7 | Lưu ảnh ở đâu (storage URL phải `https://`; khóa file không bao giờ ghi đè) | Upload, `media_assets` |
| 8 | Chấp nhận giả định "yêu cầu bị tiệm từ chối đơn thì khách phải tạo yêu cầu mới" hay cần mở rộng | Schema, Orders |
| 9 | Có quay lại EF Core khi provider hỗ trợ đủ không (hiện không dùng) | Truy cập dữ liệu về lâu dài |

Đã chốt: dùng MySqlConnector + ADO.NET với `VerifyFull`; tài khoản kết nối là `decakes_app` (đã xác minh quyền khớp `app_user_grants.sql`).

## 8. Bước tiếp theo

1. Chạy lại `dotnet run scripts/SetDbSecret.cs` để secret đúng định dạng MySqlConnector (secret cũ do bản dùng MySql.Data ghi).
2. Chốt quyết định số 1 trong bảng trên rồi triển khai Accounts (đăng ký, đăng nhập) qua `IMySqlConnectionFactory`.
3. Kiểm chứng cách đọc OUT parameter và SQLSTATE 45000 trên MySqlConnector bằng thử nghiệm có kiểm soát trước khi viết mã gọi procedure.
4. Không chạy migration, `EnsureCreated` hay lệnh thay đổi database.