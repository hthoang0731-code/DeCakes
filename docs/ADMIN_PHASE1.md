# Admin giai đoạn 1

Phạm vi: chỉ đọc quản trị và công cụ vận hành tạo user admin mới. Không thay schema, không triển khai quyết định nghiệp vụ đang mở trong [BUSINESS_RULES_REVIEW.md](BUSINESS_RULES_REVIEW.md), không sửa Catalog/Accounts/DesignRequests/Inbox.

## Hợp đồng HTTP

Tất cả endpoint dùng cookie Accounts và policy `Admin`, kiểm tra phiên mỗi request. Không có login riêng/JWT hoặc ngoại lệ session. Anonymous 401; customer/bakery 403. Admin bị disabled, xóa hoặc đổi role không dùng được cookie cũ. Lỗi trả ProblemDetails, không chi tiết database. Response thành công không cache.

| GET | Query | Response 200 |
| --- | --- | --- |
| `/api/admin/dashboard` | `isDemo?` | `isDemo`, `usersByRole`, `usersByStatus`, `bakeriesByProfileStatus`, `designRequestsByStatus`, `ordersByStatus` |
| `/api/admin/users` | `search?`, `role?`, `status?`, `isDemo?`, `page?`, `pageSize?` | Trang user |
| `/api/admin/users/{id}` | | User hoặc 404 |
| `/api/admin/bakeries` | `search?`, `profileStatus?`, `isDemo?`, `page?`, `pageSize?` | Trang tiệm |
| `/api/admin/bakeries/{id}` | | Tiệm hoặc 404 |

Trang là `{ items, page, pageSize, totalCount }`; `totalCount` là số bản ghi khớp toàn bộ bộ lọc trước phân trang, kiểu 64-bit. Dùng `Application.Common.Paging.Normalize`: `page` mặc định 1, tối thiểu 1; `pageSize` mặc định 20, từ 1–100. Admin giữ kiểu `AdminPage<T>` riêng vì `PagedResult<T>` hiện nằm trong chức năng DesignRequests. Thứ tự `id ASC`; count và items đọc bằng một câu SQL với cùng snapshot. Trang vượt cuối trả items rỗng và vẫn giữ totalCount.

`search` được trim, tối đa 255 ký tự, cấm ký tự điều khiển; rỗng coi như không lọc. Tìm chuỗi con trong email/họ tên hoặc tên/slug, theo collation schema (`utf8mb4_0900_ai_ci`, không phân biệt hoa thường/dấu). `%`, `_`, `!` được escape bằng `ESCAPE '!'`; dấu gạch chéo ngược và dấu nháy là ký tự literal. Mọi input được bind parameter, không nối vào SQL. SQL mode/escaping của kết nối phải giữ cấu hình phù hợp MySqlConnector như các store hiện hữu.

Các bộ lọc enum được trim, phân biệt hoa thường, rỗng là không lọc:

- `role`: `customer`, `bakery`, `admin`.
- User `status`: `active`, `disabled`.
- `profileStatus`: `draft`, `pending_review`, `active`, `suspended`.
- `isDemo`: `true` hoặc `false`; không truyền gồm cả demo và thật. Không áp điều kiện public Catalog lên list/detail Admin.

Id phải là số nguyên 64-bit dương. Giá trị không parse được, vượt phạm vi, 0 hoặc âm nhận 400; query sai (bao gồm enum, boolean, page, search) cũng nhận 400. Detail id hợp lệ nhưng không có nhận 404; detail không có bộ lọc demo.

User DTO gồm `id`, `email`, `fullName`, `phone`, `role`, `status`, `isDemo`, `createdAt`, `updatedAt`. Truy vấn đọc user Admin không lấy `password_hash`, kể cả detail. Bakery DTO gồm `id`, `name`, `slug`, `description`, `addressLine`, `city`, `phone`, `profileStatus`, `isDemo`, `createdAt`, `updatedAt`. List/detail cùng kiểu DTO; thời gian UTC, trường tùy chọn giữ null.

Dashboard trả mọi bucket hợp lệ, kể cả 0: user roles/status như trên; tiệm theo bốn profileStatus; yêu cầu `draft/open/ordered/cancelled`; đơn `pending_confirmation/confirmed/rejected_by_bakery/ready/picked_up/cancelled`. `isDemo` trong response ghi lại bộ lọc (null = tất cả). Users/bakeries/design_requests dùng chính cờ is_demo của dòng; orders dùng is_demo của design_request nguồn. Không suy ra demo theo customer/bakery liên quan. Tất cả bucket đọc trong một câu SQL; không tổng tiền và không gọi giá trị đơn là doanh thu. Không đọc báo giá hoặc triển khai luồng Quotes/Orders mới.

## Tạo admin bằng console

Project [DeCakes.AdminBootstrap](../tools/DeCakes.AdminBootstrap/DeCakes.AdminBootstrap.csproj) chỉ dành cho người vận hành có quyền được cấp. Không có endpoint tương ứng, không được đăng ký DI của API và không chạy tự động. Công cụ chỉ đọc `DECAKES_ADMIN_BOOTSTRAP_CONNECTION`, không tự lấy secret development, không nhận tham số ứng dụng hoặc đầu vào chuyển hướng. Không truyền connection/password bằng CLI args hoặc ghi vào repository.

1. Restore/build solution trên máy vận hành có .NET 10.
2. Cấp connection qua secret manager vào biến process `DECAKES_ADMIN_BOOTSTRAP_CONNECTION`, hoặc dùng đoạn nhập ẩn dưới đây. Kết nối phải có `SslMode=VerifyFull`, `SslCa` trỏ tới CA tồn tại, database đúng và tài khoản backend riêng có INSERT users; không dùng avnadmin. Cùng `MySqlConnectionFactory` kiểm tra cấu hình như API. Không hạ TLS để vượt lỗi.
3. Chạy trong terminal tương tác: `dotnet run --project tools/DeCakes.AdminBootstrap --no-build --no-restore`. Chương trình hỏi email, họ tên, mật khẩu ẩn và xác nhận ẩn. Mật khẩu 12–128 ký tự, không trim; email chuẩn hóa như Accounts, họ tên tối đa 150 ký tự.
4. Thành công trả exit code 0 và thông báo chung; dữ liệu sai/email trùng/lỗi trả 1. Chỉ tạo user mới `admin`, `active`, `is_demo=0`, phone null. UNIQUE email xử lý cả cạnh tranh và collation; không UPDATE tài khoản có sẵn, kể cả customer/bakery/admin disabled.
5. Xóa biến connection khỏi process sau sử dụng. Admin đăng nhập qua `/api/auth/login` hiện hữu, cùng HTTPS/cookie/CSRF.

Ví dụ PowerShell nhập connection ẩn, không nằm trong lịch sử lệnh; không dán giá trị vào chat/log:

```powershell
$savedBootstrapConnection = [Environment]::GetEnvironmentVariable('DECAKES_ADMIN_BOOTSTRAP_CONNECTION', 'Process')
$secureBootstrapConnection = Read-Host 'Connection string vận hành (nhập ẩn)' -AsSecureString
$bootstrapPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureBootstrapConnection)
try {
    [Environment]::SetEnvironmentVariable('DECAKES_ADMIN_BOOTSTRAP_CONNECTION', [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bootstrapPointer), 'Process')
    dotnet run --project tools/DeCakes.AdminBootstrap --no-build --no-restore
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bootstrapPointer)
    $secureBootstrapConnection.Dispose()
    [Environment]::SetEnvironmentVariable('DECAKES_ADMIN_BOOTSTRAP_CONNECTION', $savedBootstrapConnection, 'Process')
}
```

Không chạy công cụ lên Aiven trong task triển khai này. Các kiểm thử ghi chỉ dùng MySQL disposable loopback qua fixture; chúng không dùng biến connection vận hành và không nới TLS của factory production.

## Các file thay đổi trong task

- Application: `Admin/AdminContracts.cs`, `Admin/AdminService.cs`, `Admin/AdminBootstrapService.cs`; thêm `IAdminService` vào DI.
- Infrastructure: `Admin/MySqlAdminStore.cs`, `Admin/MySqlAdminProvisioningStore.cs`; thêm `IAdminStore` vào DI. Provisioning chỉ được console khởi tạo.
- Api: `Controllers/AdminController.cs`.
- Console: `tools/DeCakes.AdminBootstrap/{DeCakes.AdminBootstrap.csproj,Program.cs,BootstrapConsole.cs}`; thêm project vào solution.
- Kiểm thử: `AdminServiceTests`, `AdminApiTests`, `InMemoryAdminStore`, `AdminBootstrapTests`, `MySqlAdminStoreTests`, `MySqlAdminBootstrapTests`; Infrastructure tests tham chiếu console để kiểm thử luồng prompt với MySQL thật.
- Tài liệu: README, tài liệu này và tests/README. Các thay đổi DesignRequests/Inbox có sẵn, staged files và stash được giữ nguyên; không add/commit/push.

## Kiểm chứng ngày 22/10/2026

Kết quả cuối và lệnh chi tiết được ghi trong mục Admin của [tests/README](../tests/README.md). Bộ fake không chứng minh SQL; bộ persistence chạy trên MySQL Community 8.4.13 ZIP [chính thức](https://dev.mysql.com/downloads/mysql/8.4.html?os=3), checksum MD5 `7b94489d5524dea9b27097068bea25a8` khớp và chữ ký Authenticode mysqld hợp lệ (Oracle America, Inc.). Runtime riêng, bind 127.0.0.1/cổng ngẫu nhiên, X Plugin tắt, `--no-monitor`, không cài service/đổi PATH/firewall, không đụng MySQL97.

Giới hạn: chưa benchmark lượng dữ liệu lớn; tìm chuỗi con và thống kê cần quét dữ liệu. Chưa có duyệt/active/suspend tiệm, khóa user/đổi role/reset mật khẩu, quản trị đơn/báo giá, audit schema, frontend. NuGet vulnerability metadata cần mạng tới nuget.org; cảnh báo restore được ghi riêng, không coi build thành công là kiểm chứng vulnerability.
