# Kiểm thử (dự kiến)

Chưa tạo project kiểm thử trong bước tạo khung. Khi bắt đầu triển khai nghiệp vụ, thêm theo nhóm:

| Project dự kiến | Phạm vi |
| --- | --- |
| `DeCakes.Domain.Tests` | Unit test quy tắc nghiệp vụ (trạng thái yêu cầu, báo giá, đơn). Không phụ thuộc hạ tầng. |
| `DeCakes.Application.Tests` | Unit test service, dùng giả lập (mock/fake) cho interface. |
| `DeCakes.Api.Tests` | Test tích hợp endpoint bằng `WebApplicationFactory` (mã trạng thái HTTP, định dạng lỗi). |
| `DeCakes.Infrastructure.Tests` | Test gọi stored procedure trên database kiểm thử riêng, không dùng database dữ liệu thật. |

Chuỗi kết nối cho test tích hợp lấy từ user-secrets hoặc biến môi trường, không đưa vào repository.
