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

## 2. Vị trí DbContext và model scaffold

Đặt trong Infrastructure, không đặt trong Domain hay Application:

```
src/DeCakes.Infrastructure/
  Persistence/
    DeCakesDbContext.cs          DbContext (do scaffold sinh, cấu hình thêm qua partial/Configurations)
    Models/                      Class entity do scaffold sinh (model persistence)
    Configurations/              Cấu hình tay: computed column, value converter thời gian
    Procedures/                  Gọi stored procedure (xem mục 4)
```

Nguyên tắc:

- **Model scaffold là model persistence**, phản ánh bảng và cột, kể cả cột kỹ thuật (`is_demo`, `current_slot`, `agreed_*`). Chúng không phải khái niệm Domain.
- **Không sao chép máy móc toàn bộ model sang Domain.** Domain chỉ chứa khái niệm và quy tắc cần độc lập với hạ tầng (ví dụ vòng đời trạng thái yêu cầu, báo giá, đơn; định nghĩa "báo giá hết hạn"; quy tắc tiền và thời gian UTC). Application dùng DTO riêng; chuyển đổi giữa model persistence và DTO diễn ra ở Infrastructure.
- Model scaffold giữ nội bộ Infrastructure; không để Api hay Application tham chiếu chúng.
- Không sửa tay file scaffold sinh ra (sẽ mất khi scaffold lại). Mở rộng qua `partial class` hoặc `IEntityTypeConfiguration`.
- Chuỗi kết nối không nằm trong mã sinh ra (tránh `OnConfiguring` chứa chuỗi kết nối); đọc từ `ConnectionStrings:DeCakesDb` (user-secrets hoặc biến môi trường) và đăng ký trong `AddInfrastructure`.
- Cấu hình bắt buộc sau scaffold:
  - `quotes.current_slot`: cột sinh tự động (`ValueGeneratedOnAddOrUpdate`, không bao giờ gán từ ứng dụng).
  - `DATETIME(3)` đọc ra có `Kind = Unspecified`: thêm value converter gán `DateTimeKind.Utc`; chỉ ghi giờ UTC.
  - `bakery_members` có khóa chính ghép `(bakery_id, user_id)`.
  - Khóa ngoại ghép (`orders → quotes`, `quotes → request_bakeries`, `media_assets → cakes/request_bakeries`) cần kiểm tra kết quả scaffold có đúng không. **[cần xác minh]**
- Cần kiểm tra provider đã chọn (Oracle `MySql.EntityFrameworkCore` 10.0.9, đã cài ở khung dự án) có hỗ trợ scaffold đầy đủ cho các cột sinh tự động và khóa ngoại ghép. **[cần xác minh]** Scaffold chỉ đọc metadata, nhưng phải chạy với tài khoản chỉ đọc và chuỗi kết nối trong user-secrets.
- Procedure không được scaffold thành entity; bỏ qua.

## 3. Phân loại thao tác

| Loại | Cách thực hiện | Ví dụ |
| --- | --- | --- |
| Đọc (danh sách, chi tiết) | EF Core `SELECT` (nên `AsNoTracking`) | Danh sách mẫu bánh, yêu cầu của khách, báo giá của yêu cầu, đơn |
| Ghi không có procedure | EF Core `INSERT/UPDATE` bình thường, có thể nằm trong transaction EF | Tạo/sửa `design_requests`, thêm `media_assets`, đăng ký `users`, cập nhật tiệm/mẫu bánh, tiệm từ chối yêu cầu, đơn → `ready`/`picked_up`/`cancelled` |
| Ghi qua procedure tự quản lý transaction | Gọi **ngoài** transaction EF Core | 6 procedure nghiệp vụ: `sp_send_request_to_bakery`, `sp_create_quote`, `sp_revise_quote`, `sp_withdraw_quote`, `sp_accept_quote`, `sp_bakery_decide_order` |
| Procedure guard | Không gọi từ backend | 5 procedure `sp_guard_*` |

Chỉ 6 procedure nghiệp vụ trên (tự `START TRANSACTION`/`COMMIT`) bắt buộc gọi ngoài transaction EF Core, vì `START TRANSACTION` trong procedure sẽ ngầm commit transaction đang mở. Các thao tác EF thông thường ở hàng "Ghi không có procedure" không bị ràng buộc này.

## 4. Cách gọi procedure

Đề xuất (cần nhóm chốt trước khi viết mã):

1. Application định nghĩa interface theo chức năng (ví dụ cho Quotes: tạo, sửa, rút, chọn; cho Orders: xác nhận/từ chối) với tham số và DTO của Application.
2. Infrastructure triển khai bằng ADO.NET (`DbCommand`, `CommandType.StoredProcedure`) trên một connection riêng hoặc lấy từ `DbContext` khi **không** có transaction EF đang mở, khai báo OUT parameter bằng `ParameterDirection.Output`. Cách cụ thể với provider đã chọn **[cần xác minh bằng thử nghiệm]**.
3. Truyền đúng thứ tự và kiểu tham số theo [STORED_PROCEDURES.md](../database/STORED_PROCEDURES.md): `sp_create_quote` có 14 tham số, `sp_revise_quote` có 13.
4. Sau khi gọi, đọc kết quả bằng SELECT, vì procedure không trả result set.
5. Bắt `MySqlException` với SQLSTATE `45000`, lấy mã lỗi từ `Message`, và chuyển thành lỗi nghiệp vụ của Application (ví dụ một exception có thuộc tính mã). Lỗi khác SQLSTATE là lỗi hệ thống: không hiển thị nguyên văn cho người dùng. **[cần xác minh cách provider hiển thị SqlState và Message]**
6. Api ánh xạ mã lỗi sang HTTP và `ProblemDetails`. Bảng ánh xạ **[cần chốt]**: ví dụ `*_NOT_FOUND` → 404, `NOT_REQUEST_OWNER`/`NOT_BAKERY_MEMBER` → 403, `QUOTE_EXPIRED`/`REQUEST_NOT_OPEN`/`QUOTE_NOT_AVAILABLE`/`QUOTE_ALREADY_EXISTS` → 409, `INVALID_ARGUMENT`/`QUOTE_INVALID_*` → 400 hoặc 422.
7. Id người thực hiện lấy từ claim đã xác thực, không từ body.
8. Retry: chỉ retry khi gặp deadlock hoặc lock wait timeout; không retry mù khi có lỗi nghiệp vụ. Idempotency của việc gọi lặp xem từng procedure trong tài liệu procedure.

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

## 7. Quyết định cần chốt trước khi scaffold hoặc viết authentication

| # | Quyết định | Ảnh hưởng |
| --- | --- | --- |
| 1 | Cơ chế đăng nhập: ASP.NET Core Identity hay tự xây trên `users` (`PasswordHasher`); cookie hay JWT; có refresh token không. Identity cần bảng riêng, không khớp `users` hiện có nếu giữ nguyên schema | Authentication, có thể cần thay đổi schema |
| 2 | Tài khoản dùng để scaffold và chạy ứng dụng: tạo user MySQL riêng cho backend (script `app_user_grants.sql`), không dùng `avnadmin`; script quyền đã áp dụng trên Aiven chưa | Cấu hình bí mật, quyền ghi |
| 3 | Tiệm từ chối yêu cầu: dùng UPDATE trực tiếp hay thêm procedure; có cần "đã xem" | Thiếu procedure |
| 4 | "Tiệm xác nhận khả năng làm bánh" nghĩa là gì (mục 1) | Có thể cần thay đổi nghiệp vụ |
| 5 | Một user chỉ một vai trò; khách và tiệm có thể là cùng người không | Accounts |
| 6 | Bảng ánh xạ mã lỗi procedure sang HTTP và thông điệp | Api |
| 7 | Quy ước tạo tài khoản demo có mật khẩu để thử đăng nhập (dữ liệu demo hiện có `password_hash` NULL) | Môi trường phát triển |
| 8 | Lưu ảnh ở đâu (storage URL phải `https://`; khóa file không bao giờ ghi đè) | Upload, `media_assets` |
| 9 | Provider MySQL: xác nhận `MySql.EntityFrameworkCore` hỗ trợ đủ (scaffold, cột sinh tự động, OUT parameter); Pomelo chưa có bản stable cho EF Core 10 | Package |
| 10 | Chấp nhận giả định "yêu cầu bị tiệm từ chối đơn thì khách phải tạo yêu cầu mới" hay cần mở rộng (nguồn thiết kế mô tả phương án chưa triển khai) | Schema, Orders |

## 8. Bước tiếp theo an toàn cho scaffold

1. Chốt các mục 2, 3 trong bảng quyết định (tài khoản kết nối chỉ đọc và provider).
2. Đặt chuỗi kết nối vào user-secrets của project Api; kiểm tra bằng kết nối chỉ đọc, ngoài tài liệu này.
3. Chạy scaffold vào `Infrastructure/Persistence/Models` với cờ không sinh `OnConfiguring` chứa chuỗi kết nối.
4. Rà kết quả scaffold: khóa ghép, FK ghép, `current_slot`, kiểu `DATETIME(3)`; thêm cấu hình tay.
5. Đăng ký `DbContext` trong `AddInfrastructure`, chưa thêm logic nghiệp vụ; build để xác nhận.
6. Không chạy migration, `EnsureCreated` hay lệnh thay đổi database ở bất kỳ bước nào.
