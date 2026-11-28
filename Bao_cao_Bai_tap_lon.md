# BÁO CÁO BÀI TẬP LỚN

## Đề tài 09: Hệ thống đặt lịch khám và quản lý hàng đợi

## 1. Mô tả bài toán

Hệ thống cho phép bệnh nhân tra cứu bác sĩ, phòng khám, khung giờ còn trống, đặt lịch, hủy lịch và theo dõi hàng đợi. Queue Worker cấp số thứ tự bất đồng bộ. Notification Service phát sự kiện qua SignalR.

## 2. Kiến trúc

Sơ đồ kiến trúc chi tiết được lưu tại `docs/architecture.svg`.

- Patient Client gọi REST tới Appointment API.
- Appointment API đọc và ghi PostgreSQL, ghi bản tin transactional outbox.
- Outbox Dispatcher gửi sự kiện `appointment.requested` tới RabbitMQ.
- Queue Worker xử lý yêu cầu, dùng transaction và atomic upsert để cấp số.
- Worker ghi trạng thái lịch và bản tin `queue.changed` trong cùng transaction.
- Notification Service nhận sự kiện và phát tới nhóm bác sĩ qua SignalR.
- Console Client và Web App nhận cập nhật thời gian thực.

## 3. Thiết kế dữ liệu

Các thực thể chính:

- `Doctor`: thông tin bác sĩ, giờ làm việc, phòng khám và trạng thái phục vụ.
- `Room`: phòng khám, vị trí và trạng thái phục vụ.
- `Appointment`: bệnh nhân, bác sĩ, phòng khám, khung giờ, trạng thái và số thứ tự.
- `QueueCounter`: bộ đếm cuối cùng theo bác sĩ và ngày.
- `OutboxMessage`: bản tin cần phát bền vững.

## 4. Giao tiếp phân tán

- REST: tra cứu, đặt lịch, hủy lịch và cập nhật trạng thái.
- RabbitMQ: giao tiếp bất đồng bộ giữa API và Worker, Worker và Notification.
- SignalR: gửi `QueueChanged` tới client theo nhóm bác sĩ.
- Outbox Pattern: bảo đảm lịch hẹn và bản tin được lưu nhất quán.
- Retry Queue và Dead Letter Queue: phục hồi lỗi tạm thời và cô lập bản tin lỗi.
- Idempotent Consumer: bỏ qua bản tin trùng hoặc lịch không còn Pending.
- Correlation ID: `X-Correlation-ID` theo yêu cầu API và sự kiện RabbitMQ.

## 5. Cơ chế phục hồi

- API tự tạo schema và dữ liệu mẫu khi khởi động.
- Outbox dispatch tự thử lại nếu RabbitMQ không khả dụng.
- Worker và Notification Service tự kết nối lại broker.
- Publisher xác nhận bằng confirm mode.
- Hàng đợi durable và message persistent.
- Manual acknowledgement trong worker và notification consumer.

## 6. Trạng thái và phòng khám

Hệ thống hỗ trợ:

- `Available`, `Busy`, `OnBreak`, `Closed`.
- Cập nhật trạng thái bác sĩ qua `PATCH /doctors/{id}/service-status`.
- Cập nhật trạng thái phòng qua `PATCH /rooms/{id}/service-status`.
- Không nhận lịch mới khi bác sĩ hoặc phòng không ở trạng thái `Available`.

## 7. Kết quả triển khai

- Build solution thành công với 0 cảnh báo và 0 lỗi.
- 7 unit test cốt lõi đã vượt qua.
- Docker Compose cấu hình hợp lệ.
- Có Dockerfile riêng cho API, Worker và Notification.
- Có Web App và Console Client.

## 8. Hướng dẫn chạy

```powershell
Copy-Item .env.example .env
docker compose up --build
dotnet run --project src/ClinicQueue.Client
dotnet test tests/ClinicQueue.Tests/ClinicQueue.Tests.csproj
```

## 9. Đánh giá

Hệ thống đáp ứng luồng đặt lịch phân tán, cấp số bất đồng bộ, thông báo thời gian thực và phục hồi khi broker hoặc database tạm thời gián đoạn. Các phần mở rộng authentication/authorization và audit chuyên sâu có thể phát triển ở giai đoạn tiếp theo.
