# Tự test API DeCakes bằng Postman

Bộ request được đối chiếu với checkout ngày 23/10/2026, không phải cam kết toàn bộ môi trường đã sẵn sàng. Không có mật khẩu, token thật hoặc chuỗi kết nối trong các file import.

## 1. Import và gọi request đầu tiên

1. Mở **Postman Desktop** → **Import** → chọn hai file trong thư mục này:
   - [DeCakes.postman_collection.json](DeCakes.postman_collection.json)
   - [DeCakes.local.postman_environment.json](DeCakes.local.postman_environment.json)
2. Chọn environment **DeCakes — local HTTPS (writes OFF)**. `baseUrl` mặc định là `https://localhost:7215`, không thêm `/` cuối.
3. Trong terminal tại thư mục repository (`D:\DeCakes\BE` trên máy hiện tại), chạy:

   ```powershell
   dotnet run --project src/DeCakes.Api --launch-profile https
   ```

4. Trong collection, mở **00 — Bắt đầu / session → Health**, chọn **Send**. Mong đợi HTTP **200**.
5. Gửi **OpenAPI — Development**, mong đợi **200**. Sau đó gửi **Lấy CSRF**, mong đợi **200**. Script lưu token vào `csrfToken`; Postman giữ cookie tự động.

Health không kiểm tra database. Health/OpenAPI/CSRF thành công chưa chứng minh đăng nhập hoặc dữ liệu nghiệp vụ đã chạy được.

### Nếu HTTPS báo lỗi chứng chỉ

Chạy trên máy của bạn:

```powershell
dotnet dev-certs https --trust
```

Windows có thể hỏi xác nhận. Nếu Postman vẫn không tin chứng chỉ, thêm chứng chỉ HTTPS development vào phần **Settings → Certificates → CA certificates**, dùng bản public PEM của chứng chỉ đó. Không dùng CA Aiven cho `localhost`: CA Aiven dành cho kết nối API → MySQL, không phải Postman → API.

Giữ xác minh SSL bật. Không chuyển cookie authentication sang HTTP. Nếu dùng Postman Web, cần Desktop Agent để gọi localhost và quản lý chứng chỉ.

## 2. Điều kiện trước khi test đăng ký/đăng nhập và thao tác ghi

**API chạy trên localhost vẫn có thể kết nối Aiven.** Đừng bật request ghi chỉ vì thấy localhost trong URL.

Để test đầy đủ cần môi trường database kiểm thử riêng, dữ liệu dùng một lần, tài khoản hạn chế quyền, kết nối tương thích factory hiện tại (VerifyFull và CA phù hợp), schema và grants tương ứng V003–V005. Các migration nằm trong [database/migrations](../../database/migrations). Bộ Postman không chạy SQL, không seed hoặc áp dụng migration.

[README backend](../../README.md) tại thời điểm lập bộ request (23/10/2026) ghi rõ V003/V004/V005 **chưa áp dụng lên Aiven**; **cập nhật:** đã áp dụng ngày 31/10/2026 ([báo cáo](../../docs/AIVEN_DEPLOYMENT_2026-10-31.md)). Chốt ghi của collection chính giữ nguyên: API localhost vẫn dùng Aiven nên không đặt `databaseTarget=isolated-local`. Nghiệm thu email trên Aiven dùng collection riêng ở [mục 10](#10-nghiệm-thu-email-tài-khoản-collection-riêng). Khi schema chưa có V003, ngay cả login và `/me` có cookie cũng có thể lỗi vì thiếu cột mới; đây không phải lỗi Postman. Không chạy migration vào Aiven để vượt bước kiểm thử.

Chưa có môi trường riêng thì chỉ thử Health/OpenAPI/CSRF, GET Catalog phù hợp schema và các endpoint bị từ chối khi chưa đăng nhập. Chưa thể nghiệm thu luồng ghi thành công bằng Postman.

Sau khi đã xác nhận API thực sự trỏ tới database kiểm thử riêng, đặt hai biến environment:

| Biến | Giá trị |
| --- | --- |
| `writeRequestsEnabled` | `true` |
| `databaseTarget` | `isolated-local` |

Đây là lời xác nhận thủ công của người test, không phải script tự chứng minh đích database. Collection mặc định chặn POST/PUT/DELETE, kể cả login/logout vì có thể đổi phiên hoặc cập nhật hash. Không bỏ guard để chạy vào database chung.

## 3. Điền biến môi trường

Chỉ điền giá trị cục bộ trong Postman; không sửa file JSON mẫu để lưu bí mật, không chia sẻ/export environment sau khi đã điền mật khẩu hoặc token. Postman loại `secret` giúp che giá trị, không thay việc quản lý quyền chia sẻ.

| Nhóm | Biến cần điền |
| --- | --- |
| Khách | `customerEmail`, `customerPassword` |
| Chủ tiệm | `ownerEmail`, `ownerPassword` |
| Nhân viên | `staffEmail`, `staffPassword` |
| Admin có sẵn trên môi trường kiểm thử | `adminEmail`, `adminPassword` |
| Khôi phục/đổi mật khẩu, khi chạy phần này | `recoveryEmail`, `currentPassword`, `newPassword` |
| Token lấy từ email của chính người test | `verificationToken`, `resetToken`, `invitationToken` |

- Mỗi role dùng email khác nhau. Mật khẩu đăng ký/đặt mới 12–128 ký tự, không bị trim.
- Dùng hộp thư nhận được email nếu muốn kiểm thử xác minh/reset/invite thật. Email `.test` hoặc bịa không hoàn thành được luồng nhận thư.
- `planId`, `bakeryId`, `customerId`, `requestId`, `invitationId`, `staffUserId`, `cakeId`, `catalogBakeryId` được script lưu từ response khi có dữ liệu. Có thể kiểm tra/sửa ID trong environment.
- `planId` mặc định lấy gói đầu tiên nếu biến còn trống; bạn cần kiểm tra đúng gói muốn dùng. Collection không tự tạo danh mục gói khi API trả danh sách rỗng.
- `otherBakeryId` chỉ điền khi thử truy cập chéo: tiệm kiểm thử mà user hiện tại không có membership.
- `paymentId` chỉ điền khi thực sự có giao dịch trên môi trường kiểm thử. Với cổng tắt, bỏ qua request tra cứu payment.
- `pickupAt` có thể để trống: script tạo thời gian tương lai 14 ngày, ISO 8601 UTC có mili giây. Nếu tự điền phải có Z/offset và còn ở tương lai.

## 4. Cookie và CSRF: thao tác quan trọng nhất

DeCakes dùng **cookie**, không dùng Bearer token. Giữ Authorization của collection là **No Auth**, bật cookie jar mặc định; không tự chép cookie vào header hoặc file.

Trình tự:

1. GET `/api/auth/csrf` để lấy cookie và token.
2. Gửi POST login hoặc register. Các request ghi đã có header `X-CSRF-TOKEN: {{csrfToken}}`.
3. Login thành công thay cookie; script xóa biến CSRF cũ. Gửi GET CSRF lại trước request ghi tiếp theo.
4. GET `/api/auth/me` để xác nhận đúng role/email đang dùng.
5. Sau logout cũng lấy CSRF mới.

Postman dùng cookie theo domain. Đổi environment không phải là đăng xuất hoặc tạo một trình duyệt độc lập. Login admin/owner/customer trên cùng localhost sẽ thay phiên trước đó. Nếu cần thử hai phiên độc lập, dùng hai client/cookie jar độc lập và kiểm tra `/me` của từng client; không chỉ nhân đôi environment.

Request có guard báo **Thiếu biến csrfToken** là chưa gửi tới API. Gọi GET CSRF trước, không tắt bộ lọc của backend. Token không được in ra Console bởi script của collection.

## 5. Thứ tự thử luồng chính

Không bấm **Run toàn bộ collection**. Bộ có 97 request, gồm các bước login/CSRF lặp lại và các nhánh thay thế. Chạy từng request bằng Send theo bảng sau.

| Bước | Folder / thao tác | Điều kiện và kết quả chính |
| --- | --- | --- |
| 1 | 00 — Bắt đầu | Health/OpenAPI/CSRF 200; Me chưa login 401 |
| 2 | 01 — Catalog / gói | GET 200; danh sách có thể rỗng; chọn `planId` hợp lệ |
| 3 | 02 — Customer | Register 201, login 200, Me 200; lấy CSRF mới |
| 4 | 04 — Email cho customer | Lấy token từ email, confirm 204, Me có emailVerified=true |
| 5 | 03 — Chủ tiệm | Register 201 trả bakeryId; login 200, lấy CSRF mới |
| 6 | 04 — Email cho owner | Xác minh owner trước khi submit hồ sơ |
| 7 | 05 — Hồ sơ | PUT profile 200; chọn gói 204; submit 200/pending_review |
| 8 | 06 — Admin | Login admin có sẵn; lấy CSRF; chọn **APPROVE** 200 để đi tiếp |
| 9 | 07 — Owner | Login owner lại; xem subscription accessActive=true, trial 30 ngày; mời staff |
| 10 | 08 — Staff | Nhận lời mời bằng một trong hai nhánh; login staff, Me; staff bị 403 khi quản lý thành viên |
| 11 | 09 — DesignRequests | Login customer; tạo draft 201 → sửa 200 → gửi tiệm 201 → sửa sau gửi 409 |
| 12 | 10 — Inbox | Login owner hoặc staff; danh sách/chi tiết 200, tiệm không có membership 404 |
| 13 | 11 — Gói / cổng tắt | Owner đã xác minh; checkout 503 PAYMENT_NOT_CONFIGURED; billingCycle sai 400 |
| 14 | 12 / 13 / 14 | Thử mật khẩu, ca lỗi, gỡ staff/logout riêng theo điều kiện từng request |

### Các nhánh phải chọn, không chạy liên tiếp

- **Approve / request_changes / reject:** chỉ một quyết định cho một hồ sơ pending_review. Sau approve chạy quyết định nữa trả 409 là đúng. Request changes trả về draft để sửa/submit lại; reject kết thúc hồ sơ đó.
- **Accept new / accept existing:** dùng `accept-new` nếu email chưa có account; dùng `accept` khi đã có tài khoản bakery đúng email. Token một lần, không chạy cả hai với cùng token.
- **Hủy lời mời / nhận lời mời:** revoke thành công chỉ dành cho lời mời còn mở; lời mời đã nhận không còn là đối tượng để thử revoke 204.
- **Sửa draft / gửi tiệm:** phải sửa trước khi gửi. Request open không sửa được.
- **Đổi/reset mật khẩu:** cập nhật biến password của đúng role trước lần login sau. Reset không tự login và thu hồi mọi phiên; đổi mật khẩu cấp cookie mới cho phiên hiện tại.

## 6. Email chưa cấu hình: test được đến đâu?

Đăng ký có thể trả 201 dù email xác minh gửi thất bại, vì tài khoản đã được tạo. Vì vậy 201 không chứng minh Resend hoạt động.

- Resend cần `App:PublicBaseUrl`, `Email:From` và `Email:Resend:ApiKey` hợp lệ; không điền khóa vào collection.
- `resend` có thể 503 EMAIL_DELIVERY_UNAVAILABLE hoặc 429 nếu vừa gửi sau đăng ký. Throttle không phải lỗi CSRF.
- Forgot password trả 202 cả khi email không tồn tại hoặc gửi thất bại; kiểm tra hộp thư mới biết có thể đi tiếp.
- Lời mời staff có thể 201 nhưng `emailDelivered=false`; bài test gửi thư sẽ FAIL đúng để chỉ ra chưa hoàn thành luồng email.
- Link email có dạng `/verify-email#token=...`, `/reset-password#token=...`, `/accept-invitation#token=...`. Chỉ lấy giá trị sau `#token=` điền vào biến tương ứng, rồi gọi POST API. Khi chưa có frontend vẫn có thể dùng cách này nếu hệ thống gửi email đã được cấu hình đúng.
- Không đọc token từ database: hệ thống chỉ lưu digest. Không sửa email_verified_at hoặc tắt RequireVerifiedEmail để coi như luồng đã PASS.

## 7. Thanh toán chưa cấu hình

Giữ `Payments:PayOS:Enabled=false`. Collection không chứa thanh toán thật hoặc chữ ký webhook hợp lệ.

- Checkout với owner đã xác minh và billingCycle hợp lệ đang mong đợi 503 PAYMENT_NOT_CONFIGURED. Service kiểm tra cổng trước giá; chưa cấu hình không mặc định trả PLAN_NOT_PRICED.
- Khi chủ động bật cổng trên môi trường kiểm thử sau này, cần đổi kịch bản/assertion phù hợp. Gói chưa định giá không được checkout; thành công mới có paymentId/checkoutUrl.
- Payload webhook mẫu cố ý không hợp lệ: cổng tắt trả 503; không được dùng nó làm chứng minh thanh toán thành công.
- Không bấm thanh toán thật hoặc tự ký webhook để cấp kỳ sử dụng trên database chung.

Hiện chưa có API Quotes/Orders trong collection vì checkout chưa có controller cho các chức năng đó. Checkout subscription không phải đặt bánh.

## 8. Đọc lỗi nhanh

| Kết quả | Ý nghĩa / xử lý |
| --- | --- |
| Guard skip trước khi gửi | Thiếu biến hoặc chưa xác nhận môi trường ghi; xem thông báo script |
| SSL / connection refused | Kiểm tra HTTPS profile, cổng, chứng chỉ, Desktop Agent nếu dùng web |
| 400 CSRF | Lấy CSRF mới, giữ cookie jar, đúng hostname; không trộn localhost với 127.0.0.1 |
| 400 TOKEN_INVALID | Token sai/hết hạn/đã dùng/sai mục đích; lấy link hợp lệ mới |
| 401 | Chưa login, cookie hết hạn hoặc phiên đã bị thu hồi |
| 403 EMAIL_NOT_VERIFIED | Xác minh email của đúng user đang login |
| 403 OWNER_REQUIRED | Đang dùng staff thay owner |
| 404 BAKERY_NOT_FOUND | Sai bakeryId hoặc không có membership active; giữ 404 để tránh lộ tiệm khác |
| 409 | Xung đột trạng thái/email trùng/gửi trùng/không còn draft; đọc code và điều kiện của request |
| 429 | Rate limit/throttle; chờ thời gian Retry-After nếu có, đừng gửi lặp nhanh |
| 500 | Kiểm tra log đã che thông tin, schema V003–V005, grants, cấu hình database; không gửi mật khẩu/token/log nhạy cảm vào chat |
| 503 EMAIL_DELIVERY_UNAVAILABLE / PAYMENT_NOT_CONFIGURED | Chưa đủ cấu hình dịch vụ, không phải nghiệp vụ thành công |

Post-response test chỉ là điều kiện mong đợi cho kịch bản ghi trong tên/mô tả. Chạy sai role hoặc sai thứ tự sẽ FAIL; không mở rộng tất cả status thành “PASS” để che lỗi.

## 9. Nguồn và phạm vi kiểm chứng

- Hợp đồng đối chiếu trực tiếp các controller/DTO trong [src](../../src); cổng theo launchSettings HTTPS 7215.
- Import, cookie, CA certificate và pre-request skip đối chiếu với tài liệu chính thức: [Import](https://learning.postman.com/docs/getting-started/importing-and-exporting/importing-data/), [Cookies](https://learning.postman.com/docs/use/send-requests/response-data/cookies/), [Certificates](https://learning.postman.com/docs/use/send-requests/authorization/certificates/), [pm.execution](https://learning.postman.com/docs/tests-and-scripts/write-scripts/postman-sandbox-reference/pm-execution).
- File import là template trống bí mật. Không có schema/database thật được thay đổi khi tạo bộ Postman.
- Kiểm tra JSON/scripts/route và kết quả chạy test được ghi trong báo cáo bàn giao của lượt này. Không coi việc tạo collection hoặc script test là bằng chứng đã Send trong ứng dụng Postman.

### Kiểm tra thực sự đã chạy ngày 23/10/2026

| Kiểm tra | Kết quả và giới hạn |
| --- | --- |
| `dotnet build DeCakes.slnx --no-restore -m:1 -nr:false` | PASS, 0 lỗi, 1 cảnh báo NU1900 ở AdminBootstrap do không lấy được metadata vulnerability từ NuGet |
| `dotnet test tests/DeCakes.Api.Tests/DeCakes.Api.Tests.csproj --no-build --no-restore -m:1 -nr:false --logger 'console;verbosity=minimal'` | PASS: 262 đạt, 0 lỗi, 0 bỏ qua; process đã bỏ biến DECAKES_TEST_MYSQL_ADMIN và khôi phục ở finally; store/email/payment fake, có ca Kestrel HTTPS |
| Đối chiếu collection với OpenAPI của mã đã build | PASS: 97 request bao phủ 49/49 operation trong tài liệu; kiểm tra method/path, query và tên trường body. GET OpenAPI kiểm tra riêng vì tài liệu không tự mô tả endpoint của chính nó |
| JSON và JavaScript | PASS: parse hai file JSON, 151 script hợp lệ cú pháp, không dùng biến chưa khai báo; mật khẩu/token trong template trống; request ghi mặc định tắt |
| Hành vi script trước request | PASS: 6 trường hợp HTTPS/guard ghi, chặn credential trống, giữ đúng mật khẩu tổng hợp chứa dấu nháy/backslash/newline khi dựng JSON; không dùng mật khẩu thật |
| Smoke server local | Health 200, OpenAPI 200 trên HTTP loopback tạm, tắt cấu hình database/thanh toán; tiến trình kiểm tra đã dừng. Đây không phải kiểm chứng Postman HTTPS |
| Import/Send trong ứng dụng Postman; API nghiệp vụ với MySQL/Resend/payOS thật | NOT RUN trong lượt này; cần người dùng thực hiện theo hướng dẫn và môi trường ở trên |

Lượt đầu trong sandbox không kết nối được socket/testhost; kiểm thử API và smoke được chạy lại ngoài giới hạn socket rồi đạt. Không dùng Aiven để thay thế môi trường kiểm thử.

## 10. Nghiệm thu email tài khoản (collection riêng)

Hai file: [DeCakes.account-email.postman_collection.json](DeCakes.account-email.postman_collection.json) và [DeCakes.account-email.local.postman_environment.json](DeCakes.account-email.local.postman_environment.json). Chỉ gồm 29 request cho bốn luồng: đăng ký → xác minh email, gửi lại email, quên/đặt lại mật khẩu, đổi mật khẩu và thu hồi phiên. Collection chặn mọi endpoint khác và mọi host không phải `https://localhost`/`https://127.0.0.1`. Collection chính và chốt `databaseTarget` không đổi.

**Request ghi vào database mà API đang dùng:** chạy bằng `--launch-profile https` với user-secrets hiện tại là Aiven. Muốn thử trên MySQL 8.4 cục bộ thì API phải được chạy với kết nối cục bộ đã kiểm tra trước (host, cổng, database); `scopeConfirmed` không phân biệt được hai môi trường. Request ghi (đăng ký, gửi lại, xác minh, quên, đặt lại, đổi mật khẩu) bị khóa tới khi bạn đặt `scopeConfirmed = yes`, chỉ sau khi đã xác nhận phạm vi. Đăng nhập/đăng xuất/CSRF/Me không bị khóa và **luôn được gửi đi**: thiếu email/mật khẩu thì server trả lỗi hiển thị ngay ở response, không bị bỏ qua âm thầm. Khi một request bị khóa, vùng response hiện *There was an error in evaluating the Pre-request Script* kèm lý do.

### Chuẩn bị (một lần)

1. Cấu hình Resend theo [README backend → Cấu hình email](../../README.md#cấu-hình-email-resend): `dotnet run scripts/SetEmailSecret.cs` (API key nhập ẩn). Nếu dùng `onboarding@resend.dev`, email kiểm thử phải là email đăng ký tài khoản Resend.
2. Dừng API đang chạy (Ctrl+C ở terminal của nó), chạy lại `dotnet run --project src/DeCakes.Api --launch-profile https`. Để terminal mở: log ở đó cho biết Resend có nhận email hay không.
3. Postman → **Import** hai file trên → chọn environment **DeCakes — nghiệm thu email (local HTTPS)**.
4. Mở environment, điền cột **Current value** (không điền Initial value, không Export/Share sau khi điền):
   - `testEmail`: hộp thư kiểm thử của bạn, **chưa có tài khoản DeCakes**. Không dùng email admin.
   - `testPassword`, `resetPassword`, `changedPassword`: ba mật khẩu khác nhau, 12–128 ký tự.
   - Có thể thay vì biến, gõ thẳng giá trị vào Body của request đăng nhập; giá trị gõ thẳng được giữ nguyên.
5. Sau khi phạm vi đã được xác nhận: đặt `scopeConfirmed` = `yes`.
6. Postman → **Cookies** (dưới nút Send) → xóa mọi cookie của `localhost` và `127.0.0.1`. Postman giữ cookie theo host, không theo cổng hay database, nên cookie của lần chạy khác có thể bị gửi kèm.

### Thứ tự chạy

Bấm **Send** từng request theo số thứ tự. Không dùng **Run collection**. Mở tab **Test Results** sau mỗi request.

| Bước | Mong đợi | Kiểm tra thêm |
| --- | --- | --- |
| 1.1–1.2 | 200, 200 | |
| 2.1 Đăng ký | 201, `emailVerified=false` | Log API: `Resend đã nhận email email-verification, id …`; email thứ nhất tới hộp thư |
| 2.2–2.4 | 200, 200, `emailVerified=false` | |
| 2.5 Gửi lại | 202 (chờ ≥ 60 giây sau 2.1) | Log có id thứ hai; email thứ hai tới. Link trong email thứ nhất hết hiệu lực |
| 2.6 Token email thứ nhất | 400 `TOKEN_INVALID` | Chép token của **email thứ nhất** vào `oldVerificationToken`; chứng minh gửi lại thu hồi token cũ |
| 2.7 Xác minh | 204 | Chép phần sau `#token=` của **email thứ hai** vào `verificationToken` |
| 2.8 | 200, `emailVerified=true` | |
| 2.9 Dùng lại token | 400 `TOKEN_INVALID` | Chứng minh dùng một lần |
| 3.1–3.2 | 204, 200 | |
| 3.3 Quên mật khẩu | 202 | 202 luôn trả về; bằng chứng là log `password-reset, id …` và email đặt lại. 2.5–2.9 đã dùng 4 trong 5 lượt khôi phục/5 phút, nên 3.3 hoặc 3.4 có thể nhận 429 không kèm `code`: chờ rồi gửi lại |
| 3.4 Đặt lại | 204 | Chép token từ email đặt lại vào `resetToken` (hạn 30 phút) |
| 3.5 Mật khẩu cũ | 401 | |
| 3.6–3.8 | 200, 200, 200 | |
| 4.1–4.4 | 200 ×4 | Client B là `https://127.0.0.1:7215`: Postman giữ cookie theo host nên là phiên độc lập |
| 4.5 Đổi mật khẩu | 204 + cookie mới | |
| 4.6 Client A | 200 | Phiên hiện tại được cấp lại cookie |
| 4.7 Client B | 401 | Phiên ở client khác bị thu hồi |
| 4.8–4.9 | 200, 401 | Mật khẩu trước khi đổi không dùng được |
| 5.1 | 204 | |

Tổng cộng **3 email** tới hộp thư kiểm thử (2 xác minh, 1 đặt lại). Ghi trên database API đang dùng: 1 user customer mới; 3 dòng `user_tokens`; cập nhật `email_verified_at`, hai lần `password_hash` + `session_version` của chính user đó. Không đụng user khác.

### Mức bằng chứng — ghi riêng từng mức

| Mức | Bằng chứng |
| --- | --- |
| Resend chấp nhận yêu cầu | Log API `Resend đã nhận email …, id <uuid>`; id có trong Resend Dashboard → Emails |
| Email tới hộp thư | Thấy email trong hộp thư (kiểm tra cả Spam); trạng thái *Delivered* trong dashboard |
| Token được API chấp nhận | 2.7 và 3.4 trả 204 |
| Toàn bộ luồng | Test của 2.6, 2.8, 2.9, 3.5–3.8, 4.5–4.9 đều PASS |

### Lỗi thường gặp

| Hiện tượng | Nguyên nhân / xử lý |
| --- | --- |
| Log `thiếu hoặc sai cấu hình App:PublicBaseUrl…` | Chạy lại `SetEmailSecret.cs`, khởi động lại API |
| Log `Resend từ chối … HTTP 403, lỗi validation_error` | Domain chưa xác minh, hoặc dùng `resend.dev` gửi tới email khác email tài khoản Resend |
| Log `HTTP 401` hoặc `HTTP 403` với lỗi khác | API key sai/bị thu hồi/không có quyền gửi; tạo key mới, nhập lại |
| 2.5 trả 503 `EMAIL_DELIVERY_UNAVAILABLE` | Resend chưa nhận; đọc dòng log ngay trước đó |
| 2.5 trả 429 `EMAIL_RESEND_THROTTLED` | Chờ 60 giây |
| 429 không có `code` | Giới hạn theo IP của nhóm khôi phục (5 request/5 phút); chờ 5 phút |
| 2.7 trả 400 `TOKEN_INVALID` | Dùng token của email thứ nhất, chép thiếu ký tự (đủ 43 ký tự), hoặc hết hạn |
| 400 khi đăng nhập/gửi | Thường là chưa lấy CSRF mới sau đăng nhập/đăng xuất |
| Lỗi chứng chỉ ở 127.0.0.1 | Dừng bước 4 và báo lại; không tắt SSL verification |

### Sau khi xong

- Xóa Current value của mật khẩu/token trong environment. Không Export environment đã điền vào repository.
- Xóa lại cookie của `localhost` và `127.0.0.1` trước khi dùng Postman với API khác.
- Tài khoản kiểm thử còn trên database đã dùng (customer, active, đã xác minh). API không có thao tác xóa; giữ hay vô hiệu hóa là quyết định riêng.
- Báo kết quả theo từng mức bằng chứng ở trên; không gửi token, mật khẩu hoặc ảnh chụp có token.
