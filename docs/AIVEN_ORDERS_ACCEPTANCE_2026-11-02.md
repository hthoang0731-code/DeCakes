# Nghiệm thu Orders trên Aiven — 02/11/2026

## Kết quả

**PASS trong phạm vi đã duyệt:** luồng HTTPS với Controllers/service/store/procedure thật trên Aiven schema V006, gồm cả xác nhận và từ chối đơn. Lượt chạy thành công có **58 request API, 83 kiểm tra đạt, 0 lỗi**. Audit sau chạy xác nhận dữ liệu gốc giữ nguyên và có đúng 18 bản ghi mới thuộc phạm vi thử.

Email được gửi qua adapter Resend thật tới hai địa chỉ kiểm thử có nhãn riêng của lượt chạy. Trạng thái Delivered là **mô phỏng của Resend**, chưa chứng minh thư tới Inbox/Spam của một người dùng thật. Không dùng hộp thư Gmail admin làm customer/owner và không bỏ chốt xác minh email.

Run ID: `20261101185759-eec82e0a` (định danh chứa thời gian UTC; ngày báo cáo theo giờ Việt Nam).

## Những bước đã thực sự chạy

1. GET health/danh mục gói; xác minh chỉ một tiến trình DeCakes nghe loopback IPv4/IPv6 và có kết nối tới đúng service Aiven. Factory audit xác minh đúng database, backend hạn chế, VerifyFull + CA và V006; lưu baseline 17 bảng.
2. Người dùng nhập mật khẩu admin vào cửa sổ riêng có ô nhập ẩn. Đăng nhập admin và /me đạt; chưa tạo tài khoản thử trước khi bước này thành công.
3. Đăng ký owner, đọc đúng email mới trong Dashboard Resend đã đăng nhập, lấy token trong bộ nhớ và xác minh bằng endpoint bình thường. Owner đăng nhập, hoàn thiện hồ sơ, chọn gói basic, gửi duyệt; admin duyệt duy nhất tiệm mới. Đã kiểm tra trial đúng 30 × 24 giờ.
4. Đăng ký và xác minh customer bằng email thứ hai theo cùng cách, rồi đăng nhập bằng cookie jar riêng.
5. Nhánh xác nhận: customer tạo yêu cầu/gửi tiệm; owner báo giá; customer chọn báo giá tạo đơn 201 + Location; đọc bản chốt ở cả hai phía; owner confirm; customer thấy confirmed, yêu cầu ordered.
6. Nhánh từ chối: tạo yêu cầu/báo giá/đơn thứ hai; owner reject có lý do; customer thấy rejected_by_bakery, yêu cầu cancelled; báo giá đã chọn vẫn accepted.
7. Kiểm tra giá báo thực tế vượt ngân sách, giờ nhận khác yêu cầu và snapshot khớp báo giá. Kiểm tra CSRF thiếu (400), sai role (403), lý do từ chối rỗng (400), chọn lại báo giá (409 REQUEST_NOT_OPEN), quyết định lặp (409 ORDER_NOT_PENDING). Các ca sai không tạo thêm bản ghi.
8. Danh sách khách và tiệm đều có đúng hai đơn mới. Audit sau chạy kiểm tra trạng thái mới, số dòng tăng đúng phạm vi, toàn bộ cột/dấu đối chiếu các dòng gốc giữ nguyên. Đăng xuất admin/owner/customer trả 204; /me sau mỗi lần trả 401. Health cuối 200.

## Dữ liệu nghiệm thu giữ lại

| Đối tượng | ID / kết quả |
| --- | --- |
| Owner mới | user 6, role bakery, email đã xác minh |
| Customer mới | user 7, role customer, email đã xác minh |
| Tiệm mới | bakery 3, tên bắt đầu `[NGHIEM THU]`, active, basic/trial 30 ngày |
| Nhánh xác nhận | request 2 → quote 4 → order 2, confirmed |
| Nhánh từ chối | request 3 → quote 5 → order 3, rejected_by_bakery; request cancelled |

Số dòng tăng: users +2; bakeries +1; bakery_members +1; user_tokens +2 (đã dùng); bakery_subscriptions +1; bakery_subscription_periods +1; bakery_review_events +2 (submitted/approved); design_requests +2; request_bakeries +2; quotes +2; orders +2. Tổng **18 dòng trong 11 bảng**. Các bảng khác, gồm schema_migrations, không thêm dòng.

Tất cả dòng có trước lượt thử được đối chiếu lại bằng khóa chính và dấu SHA-256 tổng của các cột; không thay đổi. Không xuất dữ liệu cá nhân, token hoặc password hash ra báo cáo. Không sửa/cấp gói cho hai tiệm cũ, không tạo admin mới, không chạy migration/grants, không gọi payOS hoặc sửa SQL nguồn.

API chưa có thao tác xóa/suspend tiệm trong checkout này, nên dữ liệu nghiệm thu được giữ lại theo phạm vi đã đồng ý. Tiệm mới có thể xuất hiện trong Catalog với nhãn NGHIEM THU. Mật khẩu hai tài khoản thử được sinh ngẫu nhiên và chỉ giữ trong tiến trình, không lưu để đăng nhập thủ công sau này; việc tái sử dụng hoặc dọn các tài khoản này cần thao tác riêng được thống nhất.

## Email và dữ liệu nhập nhạy cảm

Khóa Resend hiện có chỉ gửi thư: GET thử một ID không tồn tại trả 401 restricted_api_key, trước mọi thao tác ghi. Người dùng tự đăng nhập Dashboard Resend; công cụ đọc riêng đúng hai thư theo địa chỉ nghiệm thu, không đổi quyền key hoặc đọc nội dung thư khác.

Token lấy từ thư thật của provider rồi gửi qua kênh RSA-OAEP-SHA256 cục bộ vào bộ chạy; xác minh bằng POST email-verification/confirm. Không trực tiếp đánh dấu email_verified_at hoặc lấy token từ database. Mật khẩu admin cũng nhập ẩn và chuyển bằng cùng kênh mã hóa; không nằm trong dòng lệnh/chat/log. File chứa bản mã và public key tạm đã xóa, cửa sổ nhập đã đóng; ba phiên DeCakes đã đăng xuất. Phiên Dashboard Resend của người dùng giữ nguyên.

Nguồn: [Resend test emails và labeling](https://resend.com/docs/dashboard/emails/send-test-emails). Chỉ có hai email mô phỏng trong lượt nghiệm thu; chúng tính vào quota gửi của Resend. Bằng chứng chính xác về message ID/trạng thái lưu cục bộ, không đưa nội dung thư/token vào Git.

## Công cụ, bằng chứng và giới hạn

Công cụ chỉ nằm trong `TestResults/aiven-orders-acceptance-20261102/` (Git bỏ qua): RunAcceptance.ps1, Prompt/, audit/, manifest.json, HTTP_RESULTS.json, before-audit.json, after-audit.json, hai file email-evidence.json và FINAL_CLEANUP.json. Không thay mã nguồn ứng dụng hoặc cấu hình user-secrets.

- Build cửa sổ nhập: PASS, 0 cảnh báo/0 lỗi.
- Build audit: PASS, 0 lỗi, 8 cảnh báo NU1900 do không tải được metadata vulnerability NuGet.
- Chạy bằng PowerShell 7 qua RunAcceptance.ps1 và audit trước/sau: PASS như trên. Giữ xác minh chứng chỉ HTTPS mặc định; kết nối DB dùng factory thật, VerifyFull.
- Lần chạy chuẩn bị đầu dừng trước mọi POST vì công cụ đòi một listener trong khi Kestrel có hai listener IPv4/IPv6 cùng PID. Đã sửa tiêu chí thành một tiến trình đúng đường dẫn và toàn bộ listener ở loopback, giữ các kiểm tra đích Aiven. Lượt đó chỉ có hai GET, không tạo dữ liệu/gửi thư; bằng chứng lưu riêng trong initial-listener-preflight/.
- Không chạy lại toàn bộ unit/integration test vì không sửa mã ứng dụng. Đây là bằng chứng bổ sung HTTPS/Aiven/Resend sau các kiểm thử local ở [ORDERS](ORDERS.md).
- Chưa kiểm chứng Inbox thật, nâng/gia hạn gói hay thanh toán; chưa thêm staff, ảnh, hủy đơn, ready/picked_up hoặc deadline tự động. Không có thay đổi nghiệp vụ ngoài phạm vi này.
- Không commit, push hoặc tạo PR trong lượt nghiệm thu.

Xem [triển khai V006](AIVEN_V006_DEPLOYMENT_2026-11-02.md), [Orders](ORDERS.md) và [database](../database/README.md).