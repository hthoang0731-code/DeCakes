# DeCakes — rà soát nghiệp vụ và luồng MVP

Ngày rà soát: 21/10/2026. Đối chiếu mã trong checkout hiện tại, schema và procedure SQL nguồn. Không xác nhận lại trạng thái database Aiven trong lượt này. Tài liệu này không cho phép tự áp dụng thay đổi database.

Phân biệt: **Đã chốt** là quyết định đã được xác nhận trong trao đổi; **Hiện có** là hành vi thấy trong mã/SQL; **Đề xuất** và **Cần chốt** chưa phải yêu cầu đã được phê duyệt. Không triển khai phần phụ thuộc quyết định còn mở.

## 1. Luồng chính

1. Khách xem Catalog công khai; chọn mẫu để làm cảm hứng hoặc tự mô tả ý tưởng.
2. Khách đăng nhập, tạo yêu cầu `draft`; sửa nội dung khi còn draft.
3. Khách gửi cùng yêu cầu đến từng tiệm. Lần gửi đầu chuyển yêu cầu sang `open`; nội dung bị khóa sửa ở tầng ứng dụng.
4. Thành viên tiệm đang active xem Inbox của đúng tiệm.
5. Tiệm gửi báo giá; sửa bằng phiên bản mới, hoặc rút phiên bản hiện hành.
6. Khách chọn một phiên bản báo giá còn được phép chọn. Database tạo đơn `pending_confirmation`, lưu bản chốt nội dung và chuyển yêu cầu sang `ordered`.
7. Tiệm xác nhận đơn thành `confirmed` hoặc từ chối thành `rejected_by_bakery`.
8. Nếu xác nhận: tiến trình dự kiến `confirmed -> ready -> picked_up`. Nếu từ chối: yêu cầu thành `cancelled`, khách tạo yêu cầu mới.

**Đã chốt ngày 21/10:** gửi báo giá là cam kết sơ bộ về khả năng làm bánh; xác nhận đơn sau khi khách chọn là bước chốt cuối. Không thêm trạng thái xác nhận khả năng riêng trước báo giá.

**Đã chốt từ phạm vi ban đầu:** một yêu cầu tạo tối đa một đơn, kể cả đơn đó bị từ chối. Chưa có thanh toán, giao hàng, chat hoặc AI trong luồng bắt buộc.

Chọn mẫu hiện không phải mua trực tiếp với giá cố định: API lưu `inspired_by_cake_id`, còn giá chốt đến từ báo giá. Giá Catalog là `reference_price`. Nếu cần mua mẫu ngay, phải mở phạm vi nghiệp vụ riêng.

## 2. Trạng thái và ý nghĩa

| Đối tượng | Chuyển trạng thái | Chủ thể và điều kiện |
| --- | --- | --- |
| Yêu cầu | `draft -> open` | Khách gửi lần đầu qua procedure; tiệm phải active |
| Yêu cầu | `open -> ordered` | Khách chọn báo giá qua `sp_accept_quote` |
| Yêu cầu | `ordered -> cancelled` | Tiệm từ chối đơn qua `sp_bakery_decide_order` |
| Lần gửi | `pending -> viewed` | Có trong schema, chưa có API; GET Inbox không tự ghi |
| Lần gửi | `pending/viewed -> quoted` | Procedure tạo báo giá; không có trạng thái quoted đồng nghĩa báo giá còn hiệu lực |
| Lần gửi | `pending/viewed -> declined` | Có trong schema, chưa có API; quy tắc từ chối cần chốt |
| Báo giá | `submitted -> superseded` + phiên bản mới `submitted` | Tiệm sửa bằng procedure; nội dung phiên bản cũ bất biến |
| Báo giá | `submitted -> withdrawn` | Tiệm rút; membership vẫn phải active, profile tiệm có thể không active |
| Báo giá | `submitted -> accepted` | Khách chọn; các báo giá submitted khác của yêu cầu thành not_selected |
| Đơn | `pending_confirmation -> confirmed/rejected_by_bakery` | Thành viên active của đúng tiệm; confirm cần tiệm active, reject không cần |
| Đơn | `confirmed -> ready -> picked_up` | Chưa có API/procedure; quyền chuyển và kiểm tra cạnh tranh cần triển khai |
| Yêu cầu/đơn | Sang `cancelled` do khách hoặc tiệm hủy | Chưa có API; ai được hủy ở bước nào chưa chốt |

`expired` không phải trạng thái lưu của báo giá. Cần so sánh `valid_until` với thời điểm hiện tại. `accepted` là khách đã chọn báo giá, không có nghĩa tiệm đã xác nhận đơn. Sau khi tiệm từ chối, báo giá vẫn accepted theo SQL hiện tại: giao diện phải đọc trạng thái đơn để giải thích kết quả.

## 3. Quyền theo vai trò

| Người dùng | Được làm trong phạm vi hiện tại/luồng đã định |
| --- | --- |
| Chưa đăng nhập | Xem Catalog công khai; không đọc yêu cầu/báo giá/đơn riêng tư |
| Customer | Tạo/sửa draft của mình, gửi tiệm, xem báo giá của yêu cầu mình, chọn tối đa một báo giá |
| Bakery owner/staff active | Xem và thao tác trong tiệm có membership active; bakeryId trên route không chứng minh có quyền |
| User disabled hoặc membership removed | Không được thao tác; kiểm tra lại trạng thái server |
| Admin | Có role/policy, chưa có API quản trị; không tự coi admin có mọi quyền nghiệp vụ customer/bakery |

Hiện schema và guard dùng đúng một `system_role` cho mỗi user. Cho một người vừa là khách vừa là nhân viên tiệm cần quyết định riêng, không chỉ thêm nút đổi vai trò ở giao diện.

## 4. Phần đã có và phần còn thiếu trong code

| Phần | Trạng thái checkout |
| --- | --- |
| Kết nối, Accounts customer, cookie, CSRF | Đã có |
| Catalog chỉ đọc | Đã có; ẩn cake/bakery demo |
| Tạo/sửa draft, gửi một yêu cầu đến nhiều tiệm | Đã có; mỗi lần gửi một transaction độc lập |
| Inbox tiệm và quyền đọc | Đã có; GET không đổi trạng thái |
| Đăng ký tài khoản bakery, tạo tiệm + owner | Chưa có; đây là điều kiện còn thiếu để tự sử dụng toàn luồng bằng tài khoản mới |
| Quản lý tiệm, mẫu bánh và ảnh | Chưa có API ghi; Catalog hiện chỉ đọc |
| Quotes | Có procedure, chưa có mã API/service/store trong checkout lúc rà |
| Chọn báo giá, Orders, xác nhận/từ chối đơn | Có procedure, chưa có mã API/service/store |
| Đánh dấu đã xem, từ chối yêu cầu | Có cột/trạng thái và quyền UPDATE, chưa có API |
| Ready/picked_up/hủy | Có cột/trạng thái, chưa có API và bảng chuyển trạng thái được chốt |
| Ảnh tham khảo/ảnh mẫu/ảnh thiết kế | Có media_assets, chưa có upload và storage được chọn |

Các prompt triển khai trước đây không phải bằng chứng tính năng đã tồn tại. Các kiểm thử PASS trước đây chỉ chứng minh hành vi đã được kiểm thử, không xác nhận quy tắc nghiệp vụ chưa chốt là đúng.

## 5. Các điểm cần xử lý trước khi hoàn thiện

### BR-01 — Giờ nhận bánh có thể đã qua khi chọn báo giá

**Hiện có:** guard nội dung chỉ kiểm tra `estimated_pickup_at` và `valid_until` đều ở tương lai khi tạo/sửa. Không ràng buộc quan hệ giữa hai mốc. Khi chọn, `sp_accept_quote` kiểm tra hạn báo giá, nhưng không kiểm tra lại giờ nhận; bảng orders không có CHECK `agreed_pickup_at > created_at`.

Ví dụ: báo giá tạo lúc 09:00, hẹn nhận 10:00, hết hạn 12:00. SQL nguồn có thể cho chọn lúc 11:00 và tạo đơn hẹn nhận trong quá khứ.

**Đề xuất:** `now < validUntil <= estimatedPickupAt` khi tạo/sửa; kiểm tra `estimatedPickupAt > now` lần nữa khi chọn, sau khi khóa dòng. Kiểm tra trước CALL ở API không đủ bảo vệ khoảng chờ khóa. Nếu chọn quy tắc này, cần một thay đổi procedure/migration riêng được duyệt và kiểm thử local; không sửa hoặc chạy lại SQL cài đặt trên Aiven.

**Đã chốt ngày 01/11/2026:** khi tạo/sửa báo giá, API bắt buộc `now < validUntil <= estimatedPickupAt`. Chưa triển khai phần ghi ở checkpoint đọc báo giá. Kiểm tra lại giờ nhận tại điểm chọn đơn/procedure sau khi khóa vẫn là phần việc Orders cần thiết kế và duyệt riêng; quyết định này không cho phép tự sửa SQL hoặc chạy migration Aiven. Xem [QUOTES](QUOTES.md).

### BR-02 — Hạn xử lý đơn chờ xác nhận

**Hiện có:** chưa có deadline xác nhận, job hết hạn hoặc kiểm tra thời gian nhận khi confirm. Đơn có thể chờ vô thời hạn và yêu cầu đã ở ordered.

**Cần chốt:** khách có được hủy khi chờ quá lâu không; có cần deadline tự động không. Đề xuất MVP cho khách hủy khi còn pending_confirmation và tạm chưa có tự động hết hạn. Thao tác hủy phải tranh chấp an toàn với confirm/reject.

### BR-03 — Chính sách public/demo chưa thống nhất

**Hiện có:** Catalog yêu cầu cake và bakery đều `is_demo=0`; `IsCakeAvailableForInspirationAsync` chỉ kiểm tra published/active. Procedure gửi cũng chỉ kiểm tra tiệm active, không kiểm tra demo.

**Đề xuất:** nếu dữ liệu demo đã bị ẩn khỏi luồng công khai thì thao tác truyền id trực tiếp cũng phải tuân cùng chính sách. Bổ sung điều kiện cho mẫu tham khảo và thống nhất quyền gửi đến tiệm demo. Không đổi is_demo của dữ liệu đang có để vượt điều kiện.

### BR-04 — Ngân sách và giờ khách yêu cầu là ràng buộc hay đề xuất

**Hiện có:** SQL không chặn giá báo cao hơn budget_max, cũng không bắt estimated_pickup_at khớp pickup_at của yêu cầu. Đơn lưu giá/giờ từ báo giá.

**Cần chốt:** được báo giá ngoài ngân sách và đề xuất giờ khác không. Đề xuất coi ngân sách là hướng dẫn, hiển thị rõ giá/giờ thực tế và yêu cầu khách xác nhận trước khi chọn. Đây không phải quy tắc đã được phê duyệt.

**Đã chốt ngày 01/11/2026:** cho phép giá vượt ngân sách và giờ nhận khác yêu cầu; khách phải thấy rõ giá/giờ báo thực tế trước khi chọn. Đây là quyết định mới thay cho mục cần chốt ở trên, chưa phải bằng chứng phần ghi đã được triển khai.

### BR-05 — Tạo tiệm và đưa vào hoạt động

**Hiện có:** đăng ký chỉ tạo customer; chưa có tạo user bakery, hồ sơ tiệm, owner membership hoặc admin duyệt tiệm. Database có draft/pending_review/active/suspended.

**Cần chốt:** duyệt bởi admin, hoạt động ngay, hay nhóm cấp thủ công cho MVP. Nếu có đăng ký tiệm, tạo users/bakeries/bakery_members phải nguyên tử trong transaction ADO.NET; không cho client tự gán admin hoặc membership của tiệm có sẵn.

### BR-06 — Hủy và từ chối là các thao tác khác nhau

Từ chối yêu cầu trước báo giá khác từ chối đơn sau khi khách chọn. Hủy yêu cầu chưa có đơn khác hủy đơn đã có bản chốt.

**Đề xuất cần duyệt:** khách hủy draft/open khi chưa có đơn; khách hủy đơn khi pending_confirmation; sau confirmed chưa mở tự hủy trong MVP. Các trạng thái kết thúc không được mở lại. Không dùng rejection_reason làm nơi lưu lý do hủy nếu chưa chốt mô hình dữ liệu.

### BR-07 — Đã xem và từ chối yêu cầu phải là hành động ghi rõ ràng

**Hiện có:** GET Inbox không ghi. `declined` chặn tạo báo giá, nhưng chưa có API chuyển sang declined.

**Đề xuất cần duyệt:** endpoint đánh dấu đã xem chỉ pending -> viewed; từ chối chỉ pending/viewed -> declined, có lý do. Nếu đã có báo giá, rút báo giá thay vì tự chuyển lần gửi sang declined. Khóa cùng design_request/request_bakeries theo thứ tự phù hợp procedure để tránh vượt qua trạng thái khi chạy đồng thời.

### BR-08 — Ảnh là phần còn thiếu của sản phẩm

Catalog chưa trả ảnh; yêu cầu chưa upload ảnh tham khảo; báo giá chưa có ảnh thiết kế từ API. SQL hỗ trợ ảnh nhưng chưa có storage và quyền truy cập file.

**Cần chốt:** nơi lưu và cách phục vụ HTTPS. Ảnh yêu cầu/thiết kế là dữ liệu riêng tư; URL HTTPS không tự chứng minh có phân quyền. Không dùng URL ký hết hạn làm bản chốt đơn duy nhất vì đơn cần xem lại lâu dài. Dùng khóa file bất biến, kiểm tra file thật, loại ảnh/kích thước và quyền sở hữu.

## 6. Các quy tắc đã có cần giữ

- Báo giá sửa bằng phiên bản mới; không ghi đè nội dung lịch sử.
- Báo giá hết hạn vẫn có thể được revise nếu procedure cho phép và phiên bản mới hợp lệ.
- Rút báo giá không tự đổi request_bakeries khỏi quoted; UI phải đọc trạng thái/hạn phiên bản.
- Một yêu cầu tối đa một đơn; tiệm từ chối thì không mở lại yêu cầu để chọn báo giá khác.
- Đơn dùng agreed_* làm bản chốt; không dựng lại giá/hương vị/giờ nhận từ dữ liệu đang thay đổi.
- Quyền actor lấy từ claim và được kiểm tra trong truy vấn/procedure, không từ body.
- Không tự gán quyền admin/bakery khi đăng ký customer.
- Procedure tự quản lý transaction phải gọi ngoài transaction ứng dụng; không retry mù sau lỗi mất kết nối.
- Backend có quyền UPDATE giới hạn cột không có nghĩa được chuyển trạng thái tùy ý; endpoint phải có state machine và kiểm tra đồng thời.

## 7. Thứ tự hoàn thiện sau khi chốt quyết định

1. Thống nhất các quyết định BR-01/02/04/05/06/08; ghi ai thực hiện từng thao tác và HTTP contract.
2. Sửa chênh lệch public/demo và xây tài khoản bakery/tiệm phù hợp phương án đã chọn.
3. Quotes và kiểm chứng OUT parameter, phiên bản, hết hạn, cạnh tranh.
4. Orders: chọn một báo giá, đọc bản chốt, confirm/reject và cạnh tranh giữa các hành động.
5. Ready/picked_up và các thao tác hủy đã được duyệt.
6. Upload/hiển thị ảnh; có thể làm song song bởi người thứ hai sau khi storage và quyền file được chốt.
7. Kiểm thử toàn luồng bằng customer và bakery mới, không dựa vào sửa mật khẩu demo trên Aiven. AI làm sau.

## 8. Ca nghiệm thu nghiệp vụ quan trọng

- Khách gửi cùng yêu cầu cho hai tiệm; mỗi tiệm chỉ thấy phần mình có quyền.
- Sửa draft tranh chấp với gửi: không sửa được sau khi đã open.
- Tiệm sửa báo giá; khách bấm phiên bản cũ phải bị từ chối.
- Chọn báo giá đồng thời từ hai tiệm: chỉ một đơn được tạo.
- Báo giá chưa hết hạn nhưng đã qua giờ nhận: bị chặn nếu BR-01 được duyệt.
- Tiệm suspended không nhận/gửi cam kết mới, nhưng vẫn xem lịch sử và được rút/từ chối theo quy tắc.
- Xác nhận đơn tranh chấp với hủy: chỉ một kết quả chuyển trạng thái hợp lệ.
- Từ chối đơn: giữ lịch sử, yêu cầu cancelled; khách tạo yêu cầu mới.
- Nội dung/ảnh nguồn đổi sau khi chọn không làm đổi bản chốt đơn.
- User disabled/membership removed bị mất quyền trong request tiếp theo.

## 9. Nguồn đối chiếu

- [Schema nguồn](../database/install/01_schema.sql), [procedure nguồn](../database/install/02_procedures.sql), [quyền backend](../database/security/app_user_grants.sql).
- [Catalog tiệm](../src/DeCakes.Infrastructure/Bakeries/MySqlBakeryCatalogStore.cs), [Catalog bánh](../src/DeCakes.Infrastructure/Cakes/MySqlCakeCatalogStore.cs).
- [Store yêu cầu](../src/DeCakes.Infrastructure/DesignRequests/MySqlDesignRequestStore.cs), [validation yêu cầu](../src/DeCakes.Application/DesignRequests/DesignRequestValidator.cs).
- [Store Inbox](../src/DeCakes.Infrastructure/BakeryWorkspace/MySqlBakeryWorkspaceStore.cs), [Accounts](../src/DeCakes.Application/Accounts/AccountService.cs).

Lượt này là rà soát tĩnh và viết tài liệu; không triển khai quy tắc mới, không sửa SQL nguồn và không chạy lệnh lên Aiven. Kết quả kiểm chứng kỹ thuật mới được báo riêng, không thay thế quyết định nghiệp vụ.

## 10. Kiểm chứng nền trong lượt rà soát

- `dotnet build DeCakes.slnx --no-restore -m:1 -nr:false`: PASS, 0 warning, 0 error.
- `dotnet test DeCakes.slnx --no-build --no-restore -m:1 -nr:false --filter 'FullyQualifiedName!~MySqlUserAccountStoreTests' --verbosity minimal`: PASS cho các ca không cần database, sau khi tạm bỏ biến `DECAKES_TEST_MYSQL_ADMIN` khỏi process và khôi phục ở finally. Application 139 PASS; API 154 PASS; Infrastructure 36 PASS, 37 SKIP. Tổng 329 PASS; 10 ca MySQL Accounts bị loại bằng filter, các ca MySQL còn lại SKIP.
- Các liên kết nguồn tương đối trong tài liệu: PASS, đều trỏ tới file có thật.
- Kiểm thử MySQL thật và thay đổi nghiệp vụ mới: NOT RUN trong lượt này. Chưa có thay đổi code nên không chạy lại bộ MySQL đã kiểm chứng ở lượt tích hợp trước.
