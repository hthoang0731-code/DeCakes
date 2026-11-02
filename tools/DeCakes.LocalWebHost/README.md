# Disposable local web host

Công cụ phát triển cho frontend, tách khỏi solution và API triển khai. Chạy từ FE bằng `npm run dev:local`; xem [hướng dẫn frontend](../../../FE/README.md). Dừng bằng `npm run dev:local:stop` và chờ cleanup hoàn tất.

Công cụ tái sử dụng `MySqlTestDatabase` và `WebApplicationFactory` đã có để chạy entry point, Controllers, authentication và các store thật với MySQL 8.4 riêng. Không sửa production DI, Accounts, SQL nguồn hoặc user-secrets. Fixture nạp schema/procedure/migration/grants nguồn và tự tạo/xóa database `decakes_test_*`, user `dc_test_*`. Công cụ không bootstrap admin, seed Catalog, gọi Resend/payOS, hoặc mở API/database ra ngoài loopback.

MySQL binary/TLS có sẵn dưới `.local/mysql84` cạnh BE/FE. Mỗi lần chạy có datadir riêng bên trong `TestResults/local-web/<guid>/mysql-disposable-*`; không dùng `.local/mysql84/data` đã có. Trước khi nạp fixture, xác minh server 8.4, bind/port/datadir và TLS VerifyFull. Sau đó xác minh connection factory đúng database/tài khoản của lượt này. Mật khẩu database sinh ngẫu nhiên chỉ ở bộ nhớ, không truyền trong tham số lệnh.

Local mailbox chỉ ở bộ nhớ và chỉ có trong host công cụ, không thêm endpoint vào API triển khai. Nội dung HTML được encode, không có script, dùng CSP/no-store/no-referrer. Link có token fragment để người kiểm thử tự mở trang frontend; không ghi token, hash hoặc mật khẩu vào file/log. Mailbox là một test double cho dịch vụ gửi email, không phải bằng chứng gửi thư tới nhà cung cấp hoặc Inbox thật.

Cookie dùng khóa Data Protection tạm để không chấp nhận phiên Aiven. Cookie trình duyệt vẫn có cùng tên trên hostname localhost ở các cổng khác nhau; dùng cửa sổ ẩn danh riêng. Cookie, CSRF, rate limit, session validation và thời hạn token trong API không bị nới lỏng.

Host dừng khi có `stop.request`, Ctrl+C hoặc launcher cha kết thúc. Đóng cưỡng bức toàn bộ cây tiến trình có thể ngắt cleanup; không thể bảo đảm finally chạy khi hệ điều hành kill process. Sau sự cố chỉ dọn đường dẫn do đúng lượt đó tạo, sau khi xác nhận không còn tiến trình sử dụng. Không xóa database cũ hoặc toàn bộ thư mục MySQL local.

Không đưa công cụ này vào gói triển khai/production. Nó tham chiếu các project kiểm thử có sẵn để tái sử dụng fixture và Kestrel host, không đưa các dependency đó vào project Api.
