# DeCakes — Accounts, quyền người dùng và điều kiện sử dụng của tiệm

Ngày phân tích: 22/10/2026. Đây là đặc tả làm việc và đầu bài cho Claude Code, chưa phải xác nhận chức năng đã triển khai. Các phương án có nhãn **Đề xuất** hoặc **Chưa chốt** không được tự chuyển thành yêu cầu đã duyệt.

## 1. Quyết định mới đã xác nhận

1. Ưu tiên hoàn thiện đăng ký/đăng nhập khách hàng, tài khoản và quyền của người dùng tiệm, quản trị viên.
2. Chủ tiệm tự đăng ký và tạo hồ sơ; hồ sơ phải chờ admin duyệt.
3. Tiệm phải đăng ký gói để sử dụng dịch vụ.
4. Ban đầu miễn phí 30 ngày, sau đó phải trả phí.
5. Gói và 30 ngày miễn phí tính **theo từng tiệm**; owner/staff cùng dùng quyền của gói tiệm.
6. Trial bắt đầu **khi admin duyệt tiệm**.
7. Hết hạn: chặn nhận yêu cầu, báo giá và tạo đơn mới; vẫn cho xem lịch sử, xử lý đơn đã có và gia hạn. Hết hạn gói không khóa tài khoản.
8. Thanh toán qua cổng và xác nhận tự động; mua theo giá gói với chu kỳ **tháng/năm**.
9. Owner mời/gỡ staff của chính tiệm; staff không quản lý thành viên hoặc mua/đổi gói.
10. Mỗi email giữ **một vai trò** customer/bakery/admin. Muốn vừa là khách vừa là người dùng tiệm dùng hai tài khoản với email khác nhau.
11. Đợt này **có xác minh email, quên/đặt lại mật khẩu và đổi mật khẩu**.
12. Người dùng xác nhận **chưa chốt giá**, chưa có nhà cung cấp thanh toán/email và yêu cầu đề xuất. Không tự đặt giá thật.

### Quyết định bổ sung khi triển khai (23/10/2026)

1. **Xác minh email:** tài khoản chưa xác minh vẫn đăng nhập, xem thông tin/lịch sử, gửi lại email xác minh. Phải xác minh trước khi customer tạo/gửi yêu cầu hoặc chọn báo giá tạo đơn; trước khi chủ tiệm gửi hồ sơ duyệt, mời staff hoặc checkout. Không tự đánh dấu tài khoản cũ đã xác minh. Admin bootstrap vẫn đăng nhập được; cách bắt buộc xác minh cho admin là đề xuất riêng.
2. **Review hồ sơ:** yêu cầu sửa: `pending_review → draft`, bắt buộc lý do, owner sửa và gửi lại. Từ chối: `pending_review → rejected`, bắt buộc lý do, không gửi lại hồ sơ đó (chưa có mở lại trong đợt này). Cả hai lưu người xử lý, thời gian, lý do trong lịch sử. Không khóa tài khoản/xóa membership khi bị từ chối. Chỉ approve mới bắt đầu trial. Cần migration thêm `rejected` vào CHECK, không tự áp dụng Aiven.
3. **Hồ sơ tối thiểu để gửi duyệt:** `name`, `address_line`, `city`, `phone` bắt buộc (không chỉ khoảng trắng), `description` tùy chọn, độ dài theo schema; owner đã xác minh email và đã chọn gói. Slug do server sinh, trùng xử lý bằng UNIQUE. Draft được lưu khi chưa đủ; chỉ bắt buộc đủ lúc gửi duyệt. Chọn gói không yêu cầu thanh toán trước khi duyệt.
4. **Gia hạn:** kỳ mới bắt đầu từ max(thời điểm xác nhận thanh toán, ngày hết hạn hiện tại), giữ ngày còn lại kể cả trial. +1 tháng/+1 năm theo lịch UTC, ngày không tồn tại kẹp về cuối tháng. Trial = 30 × 24 giờ từ lúc duyệt.
5. **Đổi gói:** chưa hỗ trợ khi còn hạn. Trước duyệt owner đổi gói đã chọn tự do; sau duyệt chỉ checkout gói đang dùng khi còn hạn, gói khác chỉ khi đã hết hạn. Không tính chênh lệch/hoàn tiền.
6. **Tiệm active hiện có (S-04):** migration không cấp trial hàng loạt, không dùng `updated_at`. Tiệm không có kỳ sử dụng hợp lệ bị chặn nhận yêu cầu, tạo/sửa báo giá, tạo đơn mới; không khóa tài khoản, không suspended, không hủy đơn; không có ngoại lệ "không có bản ghi gói thì miễn kiểm tra"; chưa có endpoint admin cấp trial thủ công. Ghi rõ ảnh hưởng và kế hoạch chuyển tiếp trước khi triển khai.
7. **Cổng thanh toán:** quản lý giao dịch/webhook/chống xử lý trùng độc lập nhà cung cấp + adapter payOS theo tài liệu chính thức, tắt mặc định; chỉ bật khi có merchant, giá đã chốt và URL cần thiết. Thiếu cấu hình: checkout báo lỗi rõ, không gọi payOS, không tạo link giả hay cấp kỳ; không làm lỗi khởi động/health. Fake chỉ dùng kiểm thử, không thay adapter thật trong Production.

### Đề xuất chuyển tiếp cho tiệm active hiện có (chưa duyệt)

Migration V003–V005 trong `database/migrations` là đề xuất, chưa áp dụng lên Aiven. Khi chạy V005, mọi tiệm `active` chưa có kỳ sử dụng bị chặn nhận yêu cầu, tạo/sửa báo giá và tạo đơn mới (đúng quyết định 6). Trước khi áp dụng cần nhóm chọn một cách, ví dụ:

1. Kiểm kê tiệm `active`/`is_demo = 0` thật trên Aiven bằng truy vấn chỉ đọc; xác nhận từng tiệm còn hoạt động.
2. Với tiệm được xác nhận: người vận hành chọn gói (`bakery_subscriptions`) và cấp một kỳ trial có ghi chép (ai, khi nào, lý do), bằng script chuyển tiếp riêng được review, hoặc yêu cầu tiệm thanh toán ngay khi có giá. Không dùng `updated_at` làm ngày duyệt.
3. Thông báo cho tiệm trước thời điểm chạy V005; chạy V003 → V004 → V005 và grants trong cùng cửa sổ bảo trì sau khi sao lưu; triển khai mã mới cùng lúc (mã mới đọc cột/bảng của V003–V005).
4. Sau triển khai mọi người dùng phải đăng nhập lại một lần (cookie cũ không có session version) và tài khoản cũ hiển thị chưa xác minh email.

Tự xác nhận giao dịch không đồng nghĩa tự trừ tiền định kỳ. Chưa có yêu cầu lưu phương thức thanh toán hoặc tự thu tiền sau 30 ngày. Thanh toán gói cho DeCakes khác với khách thanh toán tiền bánh cho tiệm; yêu cầu mới chưa mở phạm vi thanh toán tiền bánh.

## 2. Hiện có gì trong checkout?

| Chức năng | Bằng chứng / trạng thái |
| --- | --- |
| Khách đăng ký | POST /api/auth/register, cố định customer/active/is_demo=0; không nhận role từ client; chưa tự đăng nhập sau đăng ký |
| Đăng nhập chung | POST /api/auth/login đã chấp nhận cả customer, bakery, admin nếu user active và mật khẩu hợp lệ |
| Phiên đăng nhập | Cookie bảo mật, 2 giờ không gia hạn; kiểm tra lại user/status/role mỗi request ngoài ngoại lệ health |
| Đăng xuất / thông tin phiên | POST /api/auth/logout và GET /api/auth/me |
| CSRF và giới hạn đăng nhập | Đã có token/filter cho thao tác ghi, rate limit cho đăng ký/đăng nhập |
| Quyền tổng quát | Có policy Customer, Bakery, Admin; không đồng nghĩa có quyền với mọi bản ghi của role đó |
| Quyền đúng tiệm | Inbox lọc user active, role bakery, membership active của đúng bakery; không dựa vào bakeryId client gửi để cấp quyền |
| Admin | Dashboard, đọc users/bakeries; công cụ vận hành tạo admin mới; chưa có API duyệt hồ sơ |
| Chủ tiệm tự đăng ký | Chưa có API tạo user bakery + hồ sơ + owner membership |
| Nhân viên | Có mô hình owner/staff và active/removed; chưa có luồng cấp tài khoản/mời/gỡ nhân viên |
| Gói / trial / thanh toán gói | Chưa có bảng, dịch vụ, API, guard hoặc quyền database cho dữ liệu gói mới |
| Khôi phục / đổi mật khẩu, xác minh email | Chưa có mã; đã được xác nhận nằm trong phạm vi đợt này |

Nguồn chính: [AuthController](../src/DeCakes.Api/Controllers/AuthController.cs), [AccountService](../src/DeCakes.Application/Accounts/AccountService.cs), [AuthenticationSetup](../src/DeCakes.Api/Authentication/AuthenticationSetup.cs), [Admin giai đoạn 1](ADMIN_PHASE1.md), [schema](../database/install/01_schema.sql), [procedure](../database/install/02_procedures.sql).

Không cần xây ba hệ thống đăng nhập khác nhau. Phần thiếu là tạo đúng loại tài khoản, liên kết tài khoản với tiệm, quản trị vòng đời hồ sơ và kiểm tra điều kiện sử dụng theo gói.

## 3. Bốn lớp kiểm tra quyền phải độc lập

| Lớp | Câu hỏi | Dữ liệu quyết định |
| --- | --- | --- |
| Tài khoản website | Người này là ai, còn được đăng nhập không? | users.system_role và users.status |
| Thành viên tiệm | Người này được làm việc cho tiệm nào, với vai trò gì? | bakery_members.bakery_id/user_id/member_role/status |
| Hồ sơ tiệm | Tiệm đã được duyệt hay đang bị tạm dừng? | bakeries.profile_status |
| Quyền từ gói | Tiệm đã đăng ký gói và còn thời gian sử dụng không? | Dữ liệu gói/trial mới, thời gian hiệu lực |

- Đăng nhập thành công không tự chứng minh tiệm được nhận yêu cầu/báo giá.
- Không dùng users.status=disabled để diễn đạt tiệm đang chờ duyệt hoặc hết hạn gói.
- Không dùng bakeries.profile_status=suspended để diễn đạt hết hạn gói. Suspended là trạng thái hồ sơ/quản trị, phải được phân biệt với thanh toán.
- Không đặt subscription vào user; owner và staff của cùng tiệm dùng cùng điều kiện gói. Một user liên kết hai tiệm phải được đánh giá riêng theo từng bakeryId.
- Không nhét danh sách membership/quyền gói lâu dài vào cookie rồi tin dữ liệu đó đến hết phiên.
- Gỡ membership của một tiệm không khóa toàn bộ tài khoản và quyền ở tiệm khác.
- Admin có quyền quản trị theo endpoint được xây, không tự đóng vai khách/tiệm để đặt đơn hoặc gửi báo giá.

**Đã chốt:** hết hạn vẫn cho user bakery đăng nhập, xem lịch sử, xử lý đơn đã có và gia hạn theo quyền. **Đề xuất:** khi chờ duyệt cũng cho đăng nhập để tiếp tục onboarding; quyền sửa hồ sơ vẫn theo hợp đồng review được thống nhất.

## 4. Luồng mục tiêu theo tác nhân

### 4.1. Khách hàng

Lấy CSRF → đăng ký customer → đăng nhập → lấy lại CSRF sau khi danh tính thay đổi → /me → dùng chức năng Customer → đăng xuất.

Đó là flow hiện có. Flow cần bổ sung email xác minh và các endpoint resend/confirm/forgot/reset/change password. Chốt hợp đồng truy cập trước xác minh, gửi lại email và xử lý tài khoản cũ trước khi thay hành vi đăng nhập.

Giữ hợp đồng API hiện tại. Không cho body đăng ký chọn admin/bakery, tự đặt status/is_demo, hoặc truyền userId để xác định người sở hữu. Email trùng phải dựa vào UNIQUE của database, kể cả hai request đồng thời. Không thay đổi collation email như một phần phụ của công việc này.

### 4.2. Chủ tiệm

Luồng đã chốt ở mức nghiệp vụ:

Tự đăng ký tài khoản bakery → tạo/hoàn thiện hồ sơ và owner membership → gửi hồ sơ → chờ admin duyệt. Trial 30 ngày bắt đầu lúc admin duyệt; hết trial phải trả phí theo gói tháng/năm để tiếp tục các giao dịch mới.

Đăng ký gói là điều kiện trước khi sử dụng. **Đề xuất thiết kế:** chọn gói trong lúc hoàn thiện hồ sơ, trước khi gửi duyệt, để thời điểm duyệt có đủ thông tin kích hoạt trial. Thứ tự này chưa được người dùng xác nhận riêng; không dời mốc trial sang ngày đăng nhập đầu hoặc ngày trả tiền.

Các chi tiết thiết kế cần giải quyết trước code:

- Nếu một request tạo user/tiệm/owner/liên kết gói, các bản ghi bắt buộc phải cùng thành công hoặc rollback. Không cấp role bakery nhưng bỏ sót liên kết owner do lỗi giữa chừng.
- Nếu tách nhiều bước, mỗi bước phải có trạng thái hợp lệ và có thể tiếp tục khi trình duyệt đóng; không coi bản nháp hợp lệ là lỗi cần xóa tài khoản.
- Gói được chọn phải tồn tại và được phép đăng ký ở server. Không tin giá, số ngày miễn phí, ngày hết hạn, quyền lợi hay trạng thái thanh toán client gửi.
- Không tự chuyển tài khoản customer đang có thành bakery; đã chốt một role/email, không tự nâng role khi email trùng.
- Hồ sơ bị yêu cầu sửa/từ chối phải có cách lưu lý do và gửi lại; không tạo một tài khoản mới mỗi lần sửa hồ sơ.
- Duyệt lại hồ sơ hoặc gửi lại yêu cầu không được tự cấp thêm 30 ngày miễn phí. Quy tắc chống tạo nhiều tiệm để lặp trial vẫn cần chốt.

### 4.3. Nhân viên tiệm

Đăng nhập bằng tài khoản riêng → chọn tiệm có membership active → dùng các thao tác được cấp cho member_role và gói của đúng tiệm.

**Đã chốt:** owner mời/gỡ staff của đúng tiệm; staff không quản lý thành viên hoặc mua/đổi gói. Schema đã phân biệt owner/staff nhưng API chưa có. Không dùng chung tài khoản owner hoặc tài khoản MySQL làm tài khoản website.

Không có quyền tự tạo admin. **Đề xuất bảo vệ:** không mở chuyển owner trong đợt này; endpoint gỡ staff không áp dụng cho owner. Thiết kế cách nhận lời mời để người dùng tự đặt mật khẩu, không cho owner biết mật khẩu lâu dài của nhân viên.

Phương án mời để cân nhắc: lời mời gắn đúng tiệm và email, có hạn, dùng một lần; người nhận tự đặt mật khẩu hoặc đăng nhập bằng tài khoản bakery đã có để nhận membership. Không tự đổi customer thành bakery khi nhận lời mời. Nếu chọn phương án này, phải thiết kế nơi lưu và xác minh token, chống nhận hai lần; không bắt buộc mở toàn bộ chức năng forgot/reset chỉ để có lời mời.

### 4.4. Quản trị viên

Tạo admin bằng công cụ vận hành đã có → đăng nhập qua endpoint chung → dùng policy Admin → xem và xử lý hồ sơ chờ duyệt theo hợp đồng mới.

Không có đăng ký admin công khai. Không nâng role của tài khoản đã tồn tại chỉ vì email gửi vào trùng. Tạo admin website không có nghĩa cấp tài khoản quản trị MySQL.

Duyệt hồ sơ cần ghi người duyệt, thời gian và quyết định/lý do phù hợp. Schema hiện chưa có đầy đủ dữ liệu audit cho quy trình này. Hai admin duyệt/từ chối đồng thời phải có một chuyển trạng thái hợp lệ; không kéo dài trial do xử lý lặp.

Quản lý khóa/mở tài khoản, đổi role, quản lý admin và đối soát thanh toán là các quyền riêng; không mặc nhiên thêm mọi quyền này vào CRUD users.

## 5. Khi hết hạn gói: chính sách đã chốt và chi tiết còn mở

Người dùng đã đồng ý chặn giao dịch mới, giữ lịch sử, xử lý đơn đã có và gia hạn. Các giới hạn không phát sinh từ hết hạn như user disabled, membership removed hoặc hồ sơ suspended vẫn có hiệu lực.

| Thao tác | Chờ duyệt / chưa bắt đầu sử dụng | Hết hạn gói |
| --- | --- | --- |
| Đăng nhập, xem tình trạng hồ sơ/gói | Cho phép nếu tài khoản active | Cho phép |
| Chọn/mua/gia hạn gói | Owner của đúng tiệm theo điều kiện onboarding | Owner được gia hạn; staff không mua/đổi gói |
| Xem lịch sử riêng của đúng tiệm | Theo quyền hiện có và chính sách dữ liệu | Giữ quyền đọc lịch sử |
| Nhận yêu cầu mới; tạo/sửa báo giá; khách chọn báo giá để tạo đơn mới | Chặn nếu chưa đủ điều kiện | Chặn; cả báo giá tạo trước hết hạn cũng không được chọn để tạo đơn mới sau hết hạn |
| Xác nhận/hoàn thành đơn đã có | Chưa có cam kết mới trước khi được duyệt | Cho phép theo quyền/chuyển trạng thái hiện có; hết hạn không tự hủy đơn |
| Rút báo giá hoặc từ chối đơn đang chờ | Theo điều kiện nghiệp vụ hiện có | Đề xuất cho phép để giải phóng khách; không coi rút là tạo báo giá mới |
| Hiện trên Catalog / mẫu còn được chọn làm tham khảo | Chỉ hiện khi đủ điều kiện công khai | Chưa chốt ẩn hẳn hay vẫn hiện nhưng không nhận yêu cầu |

Đây không phải quyết định thay đổi quy tắc ready/picked_up/hủy còn mở trong đặc tả chung. Không tạo một đường bỏ qua membership, quyền user hoặc trạng thái suspended chỉ vì đang xử lý đơn cũ.

## 6. Các quyết định còn thiếu

### Cần trước khi chốt hợp đồng Accounts/onboarding

| ID | Nội dung | Tình trạng |
| --- | --- | --- |
| A-01 | Số role theo email | Đã chốt một role/email; dùng lại schema hiện tại |
| A-02 | Xác minh email, forgot/reset/change password | Đã chốt có; cần thiết kế token/mail/thu hồi phiên và xử lý user cũ |
| A-03 | Hồ sơ tối thiểu để gửi duyệt; lý do từ chối, sửa/gửi lại hồ sơ | Chưa chốt |
| A-04 | Quyền quản lý staff và cách nhận lời mời | Đã chốt owner mời/gỡ; staff không quản lý thành viên/gói. Chi tiết lời mời cần thiết kế; chuyển owner và giới hạn số tiệm chưa chốt |
| A-05 | Chọn gói trước khi gửi hồ sơ, trong lúc chờ hay sau khi duyệt | Chưa chốt thứ tự; bắt buộc có gói trước khi sử dụng |

### Cần trước khi triển khai trial/quyền gói

| ID | Nội dung | Tình trạng |
| --- | --- | --- |
| S-01 | Mốc bắt đầu 30 ngày | Đã chốt khi admin duyệt; duyệt lặp không đổi mốc cũ |
| S-02 | Hết hạn và quyền giao dịch | Đã chốt chặn giao dịch mới, vẫn đọc lịch sử/xử lý đơn đã có/gia hạn; chi tiết Catalog còn mở |
| S-03 | Trial một lần theo bakery; chống lặp khi đăng ký tiệm mới, đổi owner, gửi duyệt lại | Chưa chốt chính sách chống lạm dụng |
| S-04 | Tiệm active hiện có chưa có ngày duyệt/gói | Cần kế hoạch chuyển tiếp được thống nhất, không dùng updated_at làm ngày duyệt hoặc tự cấp trial hàng loạt |

### Cần trước khi triển khai thu phí thật

| ID | Nội dung | Tình trạng |
| --- | --- | --- |
| P-01 | Cách thanh toán | Đã chốt cổng thanh toán tự động xác nhận; chưa yêu cầu tự trừ tiền lặp. Nhà cung cấp đang được đề xuất |
| P-02 | Gói, giá và chu kỳ | Đã chốt tháng/năm; người dùng xác nhận chưa chốt giá. Tên/số gói/quyền lợi/hạn mức còn mở; không tự bịa giá thật |
| P-03 | Gia hạn sớm có giữ ngày còn lại; nâng/hạ gói; khoảng chờ; hoàn tiền | Chưa chốt |
| P-04 | Thời hạn hồ sơ/chưa duyệt khi đã trả tiền; đình chỉ khi còn kỳ trả phí | Chưa chốt |

Việc chưa chọn cổng thanh toán không ngăn phân tích hoặc xây phần Accounts độc lập đã được duyệt. Tuy nhiên không được tuyên bố đã hoàn thiện thu phí hay để tiệm sử dụng vô hạn khi hết trial chỉ vì thanh toán chưa xây.

### Đề xuất nhà cung cấp, chưa phải quyết định đã chọn

- **payOS:** đề xuất cho thanh toán gói theo lần mua tháng/năm. Có [SDK .NET chính thức](https://payos.vn/docs/sdks/back-end/net/) hỗ trợ tạo link thanh toán và xác minh webhook; [tài liệu webhook](https://payos.vn/docs/du-lieu-tra-ve/webhook/) yêu cầu tài khoản đã xác thực cá nhân/doanh nghiệp và kênh thanh toán. Phù hợp luồng owner chọn gói/kỳ → trả tiền → webhook xác thực → cấp kỳ sử dụng. Chưa khẳng định hỗ trợ tự ghi nợ định kỳ, chưa kiểm chứng phí/điều kiện thực tế của tài khoản DeCakes.
- **Resend:** đề xuất gửi email xác minh, reset và lời mời staff. Có [tài liệu/SDK .NET](https://resend.com/docs/send-with-dotnet); gửi thực tế cần [domain thuộc quyền quản lý đã xác minh](https://resend.com/docs/dashboard/domains/introduction). Không coi địa chỉ thử nghiệm của dịch vụ là địa chỉ gửi production.
- Nhóm chưa có tài khoản hai dịch vụ, chưa cung cấp domain gửi email hoặc URL HTTPS công khai cho callback/link email. Không đăng ký dịch vụ, thu tiền, gửi email thật hoặc mua domain trong lượt phân tích này.
- Giá gói của DeCakes lưu cấu hình/dữ liệu có phiên bản; gói chưa định giá không được mở checkout thật. Giá fixture chỉ ở kiểm thử, không làm giá mặc định production. Không gán giá 0 để lách việc chưa định giá.

## 7. Dữ liệu và thay đổi cần thiết

### Dùng lại

- users: email/hash/họ tên/điện thoại/role/status; cookie và PasswordHasher hiện có.
- bakeries: hồ sơ và draft/pending_review/active/suspended.
- bakery_members: owner/staff, active/removed; không xóa dòng lịch sử.
- Admin đọc và bootstrap; Inbox và các guard về user/membership/profile.

### Cần thiết kế bổ sung, chưa được áp dụng vào database

| Nhóm dữ liệu | Mục đích |
| --- | --- |
| Danh mục gói | Gói có thể đăng ký, mức giá/chu kỳ/quyền lợi sau khi được chốt |
| Đăng ký/quyền sử dụng theo bakery | Liên kết tiệm-gói; trial/kỳ trả phí; thời gian bắt đầu/kết thúc; lịch sử liên quan |
| Giao dịch thanh toán gói | Bắt buộc cho thu phí tự động: số tiền/tiền tệ đối chiếu, mã tham chiếu duy nhất, trạng thái và xử lý lặp |
| Duyệt hồ sơ | Ai quyết định, khi nào, lý do, lịch sử gửi lại cần giữ |
| Email/token/session | Bắt buộc thiết kế cho xác minh, reset, lời mời và thu hồi phiên sau thay mật khẩu; không giả định cookie hiện tại đã có cơ chế này |

Đây là nhóm thông tin cần lưu, không bắt buộc mỗi dòng là một bảng. Chốt phương thức thanh toán và hợp đồng trước khi quyết định schema tối thiểu. Không nhét ngày trial vào users hoặc dùng một cột paid=true thay cho thời hạn và lịch sử.

Giữ lại điều khoản/giá áp dụng cho kỳ đã đăng ký; sửa giá danh mục không được đổi ngược kỳ đã chốt. Cần phân biệt trạng thái ghi nhận và quyền có hiệu lực tại thời điểm hiện tại: một job chưa kịp đổi trạng thái không được giúp gói quá hạn tiếp tục dùng.

Schema hiện không có rejected cho hồ sơ. Không gửi giá trị mới này vào profile_status trước khi có thiết kế và thay đổi CHECK phù hợp, hoặc quy trình trả hồ sơ về trạng thái hiện có đã được duyệt.

Thay đổi schema/procedure/grants phải là bước được thống nhất riêng. Lưu migration tăng dần và kế hoạch triển khai; không sửa/chạy lại install hoặc tự động chạy migration vào Aiven. Quyền backend hiện có chưa cho ghi bảng gói mới.

## 8. Điểm khó khi tích hợp vào code hiện tại

1. **Không chỉ kiểm tra ở màn hình.** Backend phải bảo vệ từng thao tác; đổi bakeryId hoặc gọi HTTP trực tiếp không bỏ qua điều kiện.
2. **Không chỉ kiểm tra trước CALL.** Procedure có thể chờ khóa và gói có thể hết hạn trong lúc chờ. Khi quy tắc yêu cầu kiểm tra dưới khóa, phải thiết kế guard/transaction ở điểm ghi phù hợp; một SELECT trong C# trước CALL chưa đủ.
3. **Không thêm kiểm tra gói vào mọi guard hiện có một cách máy móc.** Guard profile đang phục vụ nhiều procedure. Chặn confirm/đơn cũ chỉ vì hết hạn gói sẽ trái chính sách vừa chốt; không thay thế điều kiện user/membership/profile hiện có.
4. **Giữ transaction của procedure.** Sáu procedure nghiệp vụ hiện tự BEGIN/COMMIT/ROLLBACK; không bọc CALL bằng transaction ADO.NET. Transaction tạo tài khoản/hồ sơ/gói là thao tác độc lập, phải có ranh giới rõ.
5. **Thời gian server quyết định.** Lưu UTC DATETIME(3), không nhận ngày hết hạn từ client. Quy tắc ở đúng mốc kết thúc và cách tính 30 ngày phải có kiểm thử sau khi chốt.
   Khách có thể mở báo giá trước lúc gói hết hạn rồi bấm chọn sau đó; phải kiểm tra lại ở điểm tạo đơn theo chính sách đã duyệt. Tiệm đăng ký gói mới hoặc sửa hồ sơ cũng không được đặt lại mốc trial cũ.
6. **Thanh toán không dựa vào trang quay về.** Chỉ ghi nhận bằng thông tin được xác thực ở server, đối chiếu tiệm/gói/kỳ/số tiền/tiền tệ và chống xử lý trùng. Webhook lặp/đến trễ/đảo thứ tự không được gia hạn hai lần; thanh toán thành công không tự mở hồ sơ suspended.
7. **Đổi mật khẩu cần thu hồi phiên thực sự.** Hiện cookie chỉ đối chiếu user/status/role. Phải thiết kế security version/stamp hoặc cơ chế phiên server tương đương. Reset/đổi hash và cập nhật dữ liệu thu hồi/tiêu thụ token cần nguyên tử; chỉ xóa cookie của một trình duyệt là không đủ.
8. **Email và token:** token ngẫu nhiên đủ mạnh, lưu digest thay giá trị thô trong kho xác thực, có mục đích/hạn/dùng một lần. Forgot/resend có rate limit và phản hồi tránh lộ email tồn tại. Người dùng disabled không được mở lại tài khoản chỉ bằng reset. Dùng origin cấu hình cho link HTTPS, không lấy tùy ý từ Host hoặc URL người dùng nhập. Email scanner mở GET không được tự đổi mật khẩu hoặc chấp nhận lời mời.
9. **Webhook là request server-to-server:** không thể yêu cầu CSRF cookie của trình duyệt. Cần ngoại lệ CSRF đúng endpoint webhook, được thay bằng xác thực chữ ký và chống xử lý lặp, không tắt filter toàn ứng dụng. Không dùng SkipSessionValidation của health cho các chức năng mới.
10. **Tài khoản cũ:** cần kế hoạch cho user hiện có chưa có trạng thái xác minh và admin bootstrap. Không tự đánh dấu mọi email đã xác minh; không khóa nhầm toàn bộ admin khi thêm điều kiện mới. Chọn và ghi rõ mức truy cập trước xác minh.

Các hướng bảo vệ token/khôi phục phiên được đối chiếu với [OWASP Forgot Password](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html) và [OWASP Session Management](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html). Đây là yêu cầu thiết kế cần Claude cụ thể hóa và kiểm thử, không phải tuyên bố code đã có.

## 9. API mới: nhóm hợp đồng để Claude đề xuất

Các endpoint dưới đây là **gợi ý tên/nhóm chức năng**, chưa tồn tại và chưa phải hợp đồng được duyệt:

| Nhóm | Hợp đồng cần thống nhất |
| --- | --- |
| Đăng ký chủ tiệm | POST /api/auth/register-bakery; hoặc quy trình nhiều bước có thể tiếp tục được |
| Hồ sơ của owner | Đọc/sửa hồ sơ và gửi duyệt của đúng tiệm; xác định trường được sửa sau khi active |
| Gói | Đọc danh mục gói; owner đăng ký; đọc điều kiện sử dụng của đúng tiệm |
| Admin review | Duyệt/yêu cầu sửa/từ chối với điều kiện trạng thái hiện tại và audit |
| Staff | Owner mời, đọc danh sách và gỡ staff đúng tiệm; nhận lời mời bằng tài khoản đúng email/role |
| Email/mật khẩu | Request/resend/confirm email; forgot/reset; đổi mật khẩu có xác thực và thu hồi phiên |
| Thu phí | Owner tạo checkout theo gói/kỳ, truy vấn trạng thái; webhook xác thực và xử lý lặp an toàn |

Giữ /api/auth/me ổn định nếu chưa có lý do đổi hợp đồng. Có thể dùng endpoint riêng trả tình trạng hồ sơ/gói của từng tiệm thay vì biến /me thành toàn bộ workspace. Backend trả mã lý do đủ để giao diện hướng tới bước tiếp theo; không đánh đồng hết hạn gói với sai mật khẩu hoặc 401.

401 cho chưa xác thực; 403 cho thiếu quyền tổng quát; tài nguyên tiệm không được quyền biết áp dụng 404 theo quy ước đang có. Mã lỗi nghiệp vụ gói/hồ sơ và trạng thái HTTP phải được thống nhất trước implementation.

## 10. Thứ tự triển khai và phạm vi file

1. **Rà và chốt hợp đồng:** giữ các chức năng Accounts đã có và các quyết định mục 1. Thiết kế phần còn thiếu của A-02..A-05/S-03 và các chi tiết Catalog/thời gian/gia hạn; không hỏi lại sáu quyết định người dùng đã trả lời.
2. **Accounts đầy đủ:** email verification, forgot/reset/change password, thu hồi phiên; tiếp theo đăng ký bakery, hồ sơ, owner membership, quyền đọc đúng tiệm và admin review. Phạm vi dự kiến: các thư mục feature trong Domain/Application/Infrastructure; controller/auth setup liên quan; hai file DI; fake và tests. Nêu chính xác file sửa trước khi triển khai, nhất là Accounts/shared infrastructure. Migration dữ liệu xác thực phải được thống nhất riêng.
3. **Gói/trial:** thiết kế schema tăng dần, quyền dữ liệu và kiểm tra thời hạn; nối review với trial đúng mốc được duyệt. Chạy trên MySQL kiểm thử riêng trước, chưa áp dụng Aiven.
4. **Thu phí:** tích hợp nhà cung cấp được chọn, dùng giá cấu hình tháng/năm; fake cho test logic và kiểm thử nhà cung cấp theo môi trường được xác nhận. Chưa định giá/merchant thì không mở thanh toán thật. Có thể tách đợt để Accounts tiếp tục, nhưng không coi fake là chứng minh cổng thật đã chạy.
5. **Nhân viên và nghiệm thu:** xây owner mời/gỡ staff và cách nhận lời mời; kiểm tra owner/staff/admin đúng quyền, bao gồm hết hạn/thay membership khi đang có cookie.

Không thêm EF Core/JWT/Identity schema đầy đủ, Generic Repository, CQRS hoặc hạ TLS để triển khai các bước này. Không thay code Catalog/DesignRequests/Quotes ngoài những predicate đã được thống nhất để bảo vệ quyền gói.

## 11. Kịch bản nghiệm thu bắt buộc cho phần được chọn

| ID | Hành vi cần chứng minh |
| --- | --- |
| AC-01 | Đăng ký public không tự cấp bakery/admin bằng trường thừa hoặc sửa request |
| AC-02 | Customer, bakery, admin đăng nhập được qua endpoint chung bằng hash thật |
| AC-03 | User disabled/hash NULL/sai mật khẩu đều không được cấp phiên; lỗi DB không biến thành lỗi mật khẩu |
| AC-04 | Customer không đọc workspace; bakery không vào Admin; admin không tự dùng quyền Customer/Bakery |
| AC-05 | Owner/staff tiệm A không thao tác tiệm B khi đổi id; removed hoặc role sai không vượt được membership |
| AC-06 | Tạo user/tiệm/owner/liên kết gói đúng ranh giới nguyên tử; lỗi hoặc đăng ký đồng thời không tạo owner/gói mồ côi |
| AC-07 | Chờ duyệt không được tự active hoặc có trial hoạt động sai mốc; status/role/giá/thời hạn do server quyết định |
| AC-08 | Duyệt/từ chối đồng thời hoặc xử lý lặp chỉ có một chuyển trạng thái hợp lệ và một lần cấp trial |
| AC-09 | Hai staff cùng tiệm dùng cùng quyền gói; một người ở hai tiệm không chuyển quyền gói từ A sang B |
| AC-10 | Hết hạn đúng biên thời gian bị xử lý theo ma trận đã duyệt, dù job nền chưa chạy |
| AC-11 | Không mất lịch sử hoặc khóa sai toàn bộ tài khoản khi tiệm hết hạn/đổi gói/gỡ một membership |
| AC-12 | Thao tác sau chờ khóa tuân thủ quy tắc gói, không chỉ vượt qua precheck C# |
| AC-13 | Đơn/báo giá phát sinh trước hết hạn được xử lý đúng quyết định; không bị chặn nhầm bởi guard dùng chung |
| AC-14 | Lặp callback/đối soát/gia hạn không cấp quyền hai lần; sai tiệm/giá/tiền tệ không kích hoạt gói |
| AC-15 | Login/logout và các thao tác ghi mới giữ CSRF, cookie, rate limit thích hợp; token trước thay đổi danh tính không được tái sử dụng |
| AC-16 | API/nhật ký không lộ hash, mật khẩu, token, chuỗi kết nối; health vẫn độc lập database |
| AC-17 | Đổi mật khẩu/thu hồi phiên chỉ được nghiệm thu nếu có cơ chế và test thực sự; không suy ra từ logout của một trình duyệt |
| AC-18 | Email verify/reset/invite sai mục đích, sai người, hết hạn, bị thu hồi hoặc dùng lại đều thất bại; hai lần dùng đồng thời chỉ một lần thành công |
| AC-19 | Forgot/resend không lộ email tồn tại, có rate limit; gửi lỗi có đường gửi lại, không tự xác minh hoặc đổi mật khẩu |
| AC-20 | Đổi/reset mật khẩu làm cookie cũ trên trình duyệt khác mất hiệu lực theo cơ chế đã thiết kế; user disabled vẫn disabled |
| AC-21 | Owner mời/gỡ staff được; staff không quản lý thành viên/mua/đổi gói; nhận lời mời không nâng customer thành bakery |
| AC-22 | Trial bắt đầu đúng thời điểm duyệt, đủ 30 ngày theo quy ước đã chọn; duyệt lặp/đổi gói/đổi owner không tự đặt lại trial |
| AC-23 | Chu kỳ tháng/năm tính theo quy ước lịch được duyệt, bao gồm cuối tháng/năm nhuận; không thay bằng 30/365 ngày một cách ngầm định |
| AC-24 | Gói chưa định giá không mở checkout thật; sửa giá danh mục không đổi số tiền/điều khoản của checkout đã chốt |
| AC-25 | Webhook không cần CSRF browser nhưng bắt buộc chữ ký; thao tác ghi khác vẫn được filter bảo vệ; webhook giả hoặc trùng không cấp quyền |

Chỉ triển khai test cho chức năng/quy tắc đã được chọn, không dùng test để hợp thức hóa đề xuất chưa duyệt. Persistence/ghi/procedure phải kiểm thử trên MySQL 8.4 loopback dùng một lần; không Aiven.

## 12. Prompt bàn giao cho Claude Code

```text
Bạn làm việc tại D:\DeCakes\BE. Đọc AGENTS.md, README.md, docs/DEVELOPMENT_WORKFLOW.md,
docs/ACCOUNTS_AUTHORIZATION_PLAN.md, docs/BUSINESS_SPECIFICATION.md và docs/ADMIN_PHASE1.md.

Mục tiêu ưu tiên: hoàn thiện Accounts cho customer/bakery/admin và onboarding tiệm.
Quyết định đã chốt: chủ tiệm tự đăng ký, tạo hồ sơ, chờ admin duyệt; phải đăng ký gói;
miễn phí ban đầu 30 ngày rồi trả phí; gói theo từng tiệm, owner/staff dùng chung.
Trial bắt đầu khi admin duyệt. Hết hạn chặn yêu cầu/báo giá/đơn mới, vẫn xem lịch sử,
xử lý đơn đã có và gia hạn. Owner mời/gỡ staff; staff không quản lý thành viên/gói.
Mỗi email một role. Có xác minh email, forgot/reset/change password và thu hồi phiên.
Thanh toán tự động qua cổng, mua theo giá gói tháng/năm; chưa yêu cầu tự trừ tiền lặp.
Giá chưa chốt: không tự bịa giá thật. payOS/Resend hiện là đề xuất, chưa có tài khoản
dịch vụ hoặc quyết định chọn. Đọc phần nhà cung cấp và các giới hạn trước tích hợp.
Login chung đã hỗ trợ cả ba role: rà và dùng lại, không viết ba cơ chế auth mới.

Trước hết đối chiếu code và SQL, cập nhật bảng đã có/còn thiếu. Đề xuất hợp đồng API,
ma trận quyền, ranh giới transaction, schema tối thiểu và kế hoạch từng bước có test.
Nêu chính xác các file Accounts/DI/shared infrastructure sẽ sửa. Giữ nguyên dirty work.
Không biến các mục Chưa chốt/Đề xuất trong đặc tả thành quyết định đã duyệt.
Chỉ triển khai bước đã được người dùng chốt và duyệt phạm vi; phần thiếu quy tắc thì
nêu câu hỏi tập trung, tiếp tục phần độc lập có ích, không tạo khung mã bỏ dở hàng loạt.

Giữ .NET 10 Controllers, ADO.NET/MySqlConnector 2.6.2, VerifyFull, cookie/CSRF,
session validation, parameterized SQL, error masking và quyền theo bakery membership.
Phân biệt user status, member status, profile status và quyền gói theo thời gian.
Không dùng hết hạn gói để khóa user hoặc tự suspended hồ sơ.

Không kết nối ghi lên Aiven; không tạo tài khoản thật, seed, thay grants/schema/procedure,
chạy install, migration tự động, commit/push/merge hoặc sửa secret.
Schema/procedure/grants mới cần bước thống nhất riêng, kiểm thử disposable MySQL 8.4
và kế hoạch áp dụng sau; sáu procedure hiện tại vẫn tự quản lý transaction.
Không mở rộng sang thanh toán tiền bánh, AI hoặc các quy tắc hủy/bàn giao chưa chốt.

Báo cáo cuối: thay đổi, quy tắc đã dùng, chính xác kiểm thử PASS/FAIL/SKIP/NOT RUN,
giới hạn, quyết định còn thiếu và phần chưa triển khai. Không dùng kết quả cũ làm bằng
chứng mới, không tuyên bố hoàn thiện thu phí nếu mới có trial hoặc trạng thái giả lập.
```

## 13. Nguồn và kiểm chứng của lượt phân tích

- Đọc mã Accounts/Auth/Admin và SQL nguồn; không mở giá trị secret, không kết nối database.
- Đối chiếu hướng dẫn [cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0), [resource-based authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0), [antiforgery](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0) của Microsoft. Policy role không thay kiểm tra quyền với từng tiệm.
- Đây là thay đổi đặc tả, không phải triển khai hoặc chạy lại bộ test ứng dụng. Kết quả chạy kiểm tra tài liệu phải được ghi riêng với phạm vi thực tế.
- Kiểm tra tài liệu của lượt này: 19 lượt liên kết nội bộ tới file tồn tại trên hai tài liệu (6 ở đây, 13 trong BUSINESS_SPECIFICATION); git diff --check đạt với phần tracked. Kiểm tra riêng hai file tài liệu về khoảng trắng cuối dòng và hàng rào code được thực hiện bằng script đọc file. Build/test ứng dụng: NOT RUN vì không đổi mã ứng dụng. Không kết nối Aiven.
