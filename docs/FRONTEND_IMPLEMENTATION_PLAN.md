# Đề xuất frontend DeCakes — 02/11/2026

Trạng thái: đề xuất công nghệ và đầu bài triển khai cho Claude Code. Workspace đã kiểm tra chỉ có backend; chưa tạo FE, cài package, khởi tạo Git hoặc thay backend. Người dùng xác nhận nhóm chưa có frontend và muốn được đề xuất. Quy tắc nghiệp vụ/backend không thay đổi.

## Công nghệ đề xuất

- React + TypeScript, Vite, React Router ở chế độ SPA đơn giản.
- npm và package-lock.json; chọn các bản stable tương thích khi tạo project, kiểm tra engines/peer dependencies, không tự dùng prerelease hoặc ghi phiên bản chưa kiểm chứng. Máy hiện có Node 24.17.0/npm 12.0.2.
- CSS Modules + bộ biến màu/khoảng cách dùng chung. Fetch API của trình duyệt, một apiClient chung; chưa thêm Redux, Axios hoặc thư viện UI lớn.
- Đề xuất project riêng tại `D:\DeCakes\FE`, song song `D:\DeCakes\BE`. Đây là lựa chọn cấu trúc được đề xuất, chưa khởi tạo repo/remote; không đưa FE vào bốn project .NET.

Lý do: backend ASP.NET Core đã sở hữu nghiệp vụ và phiên đăng nhập; frontend tập trung vào trải nghiệm thao tác. Vite cung cấp khung React/TypeScript, còn routing và việc gọi API cần thiết kế rõ. Mục tiêu trước mắt là dùng được các luồng đã có; nếu sau này cần SEO/render phía server cho Catalog thì đánh giá riêng, không coi SPA đã giải quyết nhu cầu đó.

Nguồn chính thức: [React với Vite/TypeScript](https://react.dev/learn/build-a-react-app-from-scratch), [React Router](https://reactrouter.com/start/declarative/installation), [Vite HTTPS/proxy](https://vite.dev/config/server-options).

## Kết nối và xác thực — giữ hợp đồng backend

Đọc [AuthenticationSetup.cs](../src/DeCakes.Api/Authentication/AuthenticationSetup.cs), [AuthController.cs](../src/DeCakes.Api/Controllers/AuthController.cs) và OpenAPI trước khi viết types/client. Cookie auth là HttpOnly/Secure/SameSite=Lax; cookie CSRF Secure/SameSite=Strict. API trả 401/403, không redirect HTML.

- Development đề xuất `https://localhost:5173`, chỉ nghe localhost, port cố định. Proxy giữ nguyên `/api` tới `https://localhost:7215`; browser gọi URL tương đối, không gọi MySQL/Resend trực tiếp. Vite có hỗ trợ HTTPS/proxy, nhưng không tự cấu hình sẵn mô hình này.
- Cần certificate hợp lệ cho frontend và CA tin cậy để Node xác minh HTTPS backend. Tái sử dụng chứng chỉ phát triển phù hợp nếu có; private key lưu ngoài Git. Không dùng secure:false, tắt SSL verification hoặc mở CORS rộng để vượt lỗi.
- Chưa sửa Program.cs, Accounts, DI, schema, cookie flags hoặc user-secrets của BE. Nếu phát sinh nhu cầu sửa, nêu chính xác thay đổi trước; tài liệu này không cấp quyền sửa backend.
- Production cần cùng public origin với `/api` được chuyển tới backend và các route SPA được phục vụ đúng. Triển khai/reverse proxy là nhiệm vụ riêng; Vite proxy chỉ dành cho dev. Không biến lỗi `/api` thành HTML index.html.
- apiClient dùng credentials cùng origin. Lấy CSRF từ GET `/api/auth/csrf`, gửi `X-CSRF-TOKEN` cho thao tác ghi; lấy token mới sau login/logout. Chỉ giữ token CSRF trong bộ nhớ; không đọc cookie HttpOnly, không tạo JWT hoặc lưu mật khẩu/token xác thực trong localStorage.
- AuthContext lấy user thật từ GET `/api/auth/me`. Lúc khởi động, 401 là trạng thái khách vãng lai. Login chuyển theo role customer/bakery/admin; không có bộ chọn giả lập role và không có đăng ký admin công khai. Route guard hỗ trợ UX, quyền thật vẫn do backend kiểm tra.
- Xử lý ProblemDetails và ValidationProblemDetails theo trường/code. Với 429, tôn trọng Retry-After; lỗi mạng giữ dữ liệu biểu mẫu phù hợp, không tự retry POST/PUT. Session hết hạn phải cập nhật UI và cho đăng nhập lại, không gán nhầm 500 thành logout.
- Link xác minh/đặt lại mật khẩu đang dùng `#token=`. Đọc vào bộ nhớ, xóa fragment bằng history.replaceState, không ghi log/analytics/localStorage. Xác minh bằng nút chủ động gửi POST với CSRF. App:PublicBaseUrl hiện phục vụ link backend; việc đổi sang origin frontend cần cấu hình riêng được thống nhất, không sửa secret âm thầm.

## Các đợt triển khai

| Đợt | Kết quả sử dụng được | Giới hạn |
| --- | --- | --- |
| 1 — Nền tảng, Accounts, Catalog | Khung responsive, điều hướng, đăng nhập/đăng xuất/đăng ký customer và owner, xác minh email/quên-đặt lại mật khẩu, danh mục/chi tiết bánh-tiệm; khung workspace theo role | Chưa có form yêu cầu/báo giá/đơn hoặc quản trị ghi |
| 2 — Customer | Tạo/sửa draft, gửi tiệm, xem báo giá và lịch sử, chọn báo giá, đọc đơn | Không có upload ảnh, hủy yêu cầu/đơn, thanh toán tiền bánh hoặc AI |
| 3 — Bakery | Hồ sơ/chọn gói/gửi duyệt, trạng thái trial, chọn tiệm từ membership thật, inbox, tạo/sửa/rút báo giá, đọc đơn và confirm/reject | Không có trạng thái mới, reject yêu cầu trước báo giá, ready/picked_up hoặc tạo gói có giá giả |
| 4 — Admin | Dashboard thật, danh sách/chi tiết user-tiệm, hàng chờ duyệt và approve/request_changes/reject có lý do theo API | Không tự thêm khóa tài khoản, suspend tiệm, sửa giá gói hay xóa dữ liệu khi API chưa có |

Đợt 1 cần hoàn thành và nghiệm thu độc lập trước khi chạy các đợt sau. Không giao cả bốn đợt như một lần viết toàn bộ ứng dụng.

## Đầu bài cụ thể cho đợt 1

Tạo cấu trúc nhỏ theo chức năng: `src/app` (router/providers), `src/features/auth`, `src/features/catalog`, `src/shared/api`, `src/shared/ui`, `src/styles`. Không tạo sẵn các tầng rỗng hoặc framework nội bộ.

Các trang đề xuất:

- `/`: trang vào có lời giới thiệu ngắn và Catalog thật; không bịa số lượng đơn, đánh giá hay doanh thu.
- `/cakes`, `/cakes/:id`, `/bakeries`, `/bakeries/:id`: loading/empty/error, tìm kiếm/phân trang đúng tham số API. Dùng ảnh hợp lệ từ DTO nếu có, nếu thiếu thì placeholder trung tính; không tự sinh URL ảnh riêng tư.
- `/login`, `/register`, `/register-bakery`, `/verify-email`, `/forgot-password`, `/reset-password`.
- `/customer`, `/bakery`, `/admin`: khung điều hướng bảo vệ theo role và thông tin tài khoản thật, ghi rõ các mục chưa triển khai thay vì nút giả thành công. Không cần gọi Admin dashboard trong đợt này.
- Trang 403/404; tải lại deep link vẫn hoạt động ở dev. Giao diện tiếng Việt, đọc được trên mobile, label rõ, hỗ trợ bàn phím, focus và thông báo lỗi gắn đúng trường.

Đăng ký không tự đăng nhập nếu API không làm vậy. HTTP 201 khi đăng ký chỉ chứng minh đã tạo tài khoản; không được báo chắc chắn đã nhận email. Chủ tiệm mới ở draft/pending_review phải thấy trạng thái hồ sơ, không bị mô tả như đã được duyệt. Một email có một role; người muốn hai vai trò cần hai tài khoản khác nhau.

Thiết kế đề xuất: nền kem, điểm nhấn hồng đất và màu chocolate, chữ có độ tương phản tốt; thanh điều hướng rõ. Ưu tiên thao tác và nội dung thật, chưa cần ảnh hero AI hoặc dashboard trang trí bằng số liệu giả.

## Các quy tắc phải thể hiện ở đợt nghiệp vụ

- Customer chỉ sửa draft; giá báo có thể cao hơn ngân sách và giờ nhận khác yêu cầu. Trước khi chọn, hiện rõ điều kiện thực tế và xác nhận của người dùng.
- Một yêu cầu tối đa một đơn. Đơn bị từ chối thì giải thích khách cần tạo yêu cầu mới; không hiển thị nút chọn lại báo giá khác.
- Nội dung đơn đọc từ agreed_*; không dựng lại từ báo giá hiện tại. Hạn báo giá/giờ nhận hiển thị rõ; phía server quyết định tại thời điểm ghi. Hiển thị giờ Việt Nam và chuyển input sang ISO UTC có offset/Z, chính xác mili giây.
- Mất kết nối sau accept: đọc GET đơn theo request để đối chiếu; sau decision: đọc lại đơn. Không tự gửi lại lệnh ghi.
- Inbox GET không đánh dấu viewed; không trình bày như đã có thao tác ghi nhận đã xem.
- Tiệm hết gói/suspended vẫn có thể xem lịch sử theo quyền thật. Confirm/reject đơn cũ theo quy tắc backend; reject cần lý do, confirm sau giờ nhận bị chặn. Email/gói/membership không được giả lập trong frontend.
- Gói basic chưa có giá, payOS chưa mở: thể hiện trạng thái chưa mở thanh toán, không đổi NULL thành 0đ hoặc hiển thị thanh toán thành công giả. Trial hiển thị theo startsAt/endsAt của server.
- Admin chỉ có những thao tác API hiện hỗ trợ. Không tự bổ sung endpoint hoặc sửa schema để làm đủ màn hình.

## Chia việc cho hai người

Người phụ trách nền tảng sở hữu router, apiClient, AuthContext và các component chung; làm Customer sau đợt 1. Người còn lại làm Bakery/Admin trong feature folders sau khi hợp đồng chung đã ổn định. Thống nhất route/type trước khi cùng sửa; không để cả hai tự tạo hai cơ chế đăng nhập/CSRF. Đây là đề xuất phân công, chưa gửi nhiệm vụ cho ai.

## Tiêu chí nghiệm thu đợt 1

- Chọn dependency stable tương thích, lưu lockfile; npm ci, typecheck, lint và build đạt bằng script được khai báo thực tế.
- Kiểm thử hành vi quan trọng: 401/403, phiên hết hạn, CSRF trước/sau đổi danh tính, validation hiển thị đúng trường, 429, xóa token khỏi URL, không retry thao tác ghi, loading/empty/error và quyền route.
- Chạy trình duyệt HTTPS thật, refresh/deep link, desktop/mobile, cookie/CSRF qua proxy; không tắt xác minh chứng chỉ.
- Mặc định kiểm thử bằng mock HTTP rõ ràng hoặc backend MySQL 8.4 disposable. Không dùng tài khoản/dữ liệu nghiệm thu Aiven để tự chạy lại đăng ký hoặc ghi dữ liệu; nghiệm thu ghi Aiven cần phạm vi mới. Test mock phải được ghi nhãn, không coi là bằng chứng store/Resend thật.
- Không cần chạy lại mọi bộ kiểm thử backend cho việc chỉ thêm tài liệu/FE, trừ khi đã được duyệt sửa backend và thay đổi đó cần kiểm chứng.
- Báo rõ file thay đổi, lệnh đã chạy, PASS/FAIL/NOT RUN và ảnh/chứng cứ UI không chứa secret. Không commit/push, mua domain, deploy hay tạo repo GitHub khi chưa được yêu cầu.

Tham chiếu: [Accounts và phân quyền](ACCOUNTS_AUTHORIZATION_PLAN.md), [Orders](ORDERS.md), [nghiệm thu Aiven](AIVEN_ORDERS_ACCEPTANCE_2026-11-02.md), [API Controllers](../src/DeCakes.Api/Controllers), [quy trình BE](DEVELOPMENT_WORKFLOW.md).