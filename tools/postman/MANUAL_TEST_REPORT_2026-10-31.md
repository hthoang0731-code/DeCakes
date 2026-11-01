# Kiểm tra thủ công Postman — 31/10/2026

Đã thao tác trực tiếp trong Postman Desktop trên máy người dùng, sử dụng API đang chạy tại `https://localhost:7215`. Không thay code backend hoặc cấu hình database.

## Chuẩn bị đã thực hiện

- Import thành công collection `DeCakes — API manual tests (2026-10-23)` và environment `DeCakes — local HTTPS (writes OFF)` từ hai file mẫu trong thư mục này.
- Kiểm tra environment mẫu không có thông tin cá nhân, mật khẩu hoặc token trước khi import.
- Chọn environment DeCakes; giữ nguyên chốt tắt POST/PUT/DELETE. Không điền tài khoản hoặc khóa dịch vụ.

## Kết quả thực tế

| Request | HTTP | Xác minh |
| --- | --- | --- |
| GET /api/health | 200 | Body trả status Healthy, service DeCakes.Api |
| GET /openapi/v1.json | 200 | Tài liệu OpenAPI 3.1.1; assertion HTTP 200 PASSED |
| GET /api/auth/csrf | 200 | Assertion HTTP 200 và headerName X-CSRF-TOKEN đều PASSED; script lưu biến csrfToken trong Postman |

Không chép token hoặc cookie vào báo cáo. Màn hình Postman được để ở Test Results của CSRF, hiển thị hai ca PASSED; không để nội dung token mở trên màn hình.

Health và tài liệu OpenAPI cũng được mở và xác nhận bằng trình duyệt trong ứng dụng. Các kết quả này không chứng minh các endpoint nghiệp vụ hoặc kết nối database đã được nghiệm thu.

## Phạm vi chưa chạy

- Không gửi đăng ký/đăng nhập, đổi/reset mật khẩu, duyệt hồ sơ, mời/gỡ staff, gửi yêu cầu hoặc checkout/webhook.
- Không kiểm tra hoặc thay đổi schema/migration/grants trên Aiven, không ghi dữ liệu nghiệp vụ, không gửi email hoặc thanh toán thật.
- Không chạy lại toàn bộ bộ test tự động trong lượt thao tác UI này. Kết quả 262 API tests ở báo cáo 23/10 là kết quả lịch sử riêng.
- Không bật tắt xác minh TLS hoặc thay đổi thiết lập bảo mật.
- Không commit/push.

Ảnh xác nhận được lưu cục bộ trong `TestResults/postman-validation/postman-smoke-20261031.png` (thư mục đã bị Git bỏ qua).
