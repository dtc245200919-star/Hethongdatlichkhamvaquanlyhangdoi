# Hệ thống đặt lịch khám và quản lý hàng đợi

Đề tài 09 — ứng dụng phân tán dùng .NET 10 Minimal API, PostgreSQL, RabbitMQ và SignalR.

## Kiến trúc

```text
Console Client --REST--> Appointments API --transaction--> PostgreSQL
      |                         |
      |                         +-- Outbox --> RabbitMQ appointment requests
      |                                              |
      |                                        Queue Worker
      |                                              |
      |                         PostgreSQL <-- transaction + queue counter
      |                                              |
      +<-- SignalR <-- Notification Service <-- RabbitMQ clinic.events
```

- **Appointments API** quản lý bác sĩ, khung giờ, lịch khám và trạng thái hủy; đồng thời cung cấp API tra cứu hàng đợi.
- **Queue Worker** nhận yêu cầu bất đồng bộ, cấp số thứ tự theo bác sĩ/ngày bằng cập nhật nguyên tử trong PostgreSQL.
- **Notification Service** nhận sự kiện hàng đợi từ RabbitMQ và phát sự kiện `QueueChanged` qua SignalR tới nhóm bác sĩ tương ứng.
- **Console Client** gọi REST API và đăng ký SignalR để minh họa luồng cập nhật trực tiếp.
- PostgreSQL và RabbitMQ chạy riêng; các service gọi nhau bằng service name trong Docker network.

### Phục hồi và tính nhất quán

- API lưu lịch khám và bản tin outbox trong cùng transaction. Khi RabbitMQ dừng tạm thời, outbox vẫn được giữ và service tự thử gửi lại.
- Queue Worker dùng manual acknowledgement, consumer idempotent theo trạng thái lịch khám, publisher confirm và retry queue có TTL 5 giây; sau 5 lần thử lại, bản tin được chuyển vào `clinic.appointment.dead`.
- Cập nhật trạng thái lịch, bộ đếm số thứ tự và sự kiện outbox được commit cùng transaction.
- RabbitMQ dùng durable queue/exchange và bản tin persistent; Notification Service tự kết nối lại khi broker phục hồi.
- API và Notification Service có endpoint `/health`; PostgreSQL có health check trong Compose.
- Logging ghi nhận nhận lịch, cấp số, gửi outbox, kết nối broker và lỗi xử lý.

## Yêu cầu

- .NET SDK 10
- Docker Desktop với Docker Engine đang chạy
- Git

## Cấu hình và khởi chạy

Từ thư mục repository:

```powershell
Copy-Item .env.example .env
```

Sửa `POSTGRES_PASSWORD` và `RABBITMQ_PASSWORD` trong `.env` trước khi dùng/chia sẻ môi trường. Không commit file `.env`.

Khởi chạy toàn bộ service:

```powershell
docker compose up --build
```

Lần đầu, API tạo schema và dữ liệu mẫu gồm ba bác sĩ. Các cổng mặc định:

| Thành phần | URL |
|---|---|
| Appointment API | `http://localhost:8080` |
| Notification Service / SignalR | `http://localhost:8082/hubs/notifications` |
| RabbitMQ Management | `http://localhost:15672` |
| PostgreSQL | `localhost:5432` |

Theo dõi log:

```powershell
docker compose logs -f appointments-api queue-worker notification-service
```

Chạy client console trong cửa sổ terminal khác:

```powershell
dotnet run --project src/ClinicQueue.Client
```

Client kết nối các URL mặc định ở trên. Nếu đã đổi cổng, đặt `APPOINTMENT_API_URL` và `NOTIFICATION_HUB_URL` trong môi trường terminal trước khi chạy.

Dừng các service nhưng giữ dữ liệu:

```powershell
docker compose down
```

Xóa cả volume database (thao tác này xóa dữ liệu lịch khám):

```powershell
docker compose down -v
```

## Build và kiểm tra

```powershell
dotnet build ClinicQueue.sln
docker compose config
```

Docker phải đang chạy để build image hoặc kiểm thử tích hợp qua Compose.

## API tối thiểu

| Phương thức | Đường dẫn | Mô tả |
|---|---|---|
| `GET` | `/doctors` | Danh sách bác sĩ |
| `GET` | `/doctors/{id}/slots?date=yyyy-MM-dd` | Khung giờ còn trống trong tối đa 30 ngày |
| `POST` | `/appointments` | Đặt lịch; trả `202 Accepted` trong khi worker cấp số |
| `DELETE` | `/appointments/{id}` | Hủy lịch |
| `GET` | `/appointments/{id}` | Tra cứu lịch và trạng thái |
| `GET` | `/queues/{doctorId}?date=yyyy-MM-dd` | Tra cứu hàng đợi trong ngày |

Ví dụ đặt lịch (giờ khám là giờ địa phương của phòng khám, không kèm múi giờ):

```json
{
  "patientName": "Nguyen Van A",
  "patientPhone": "0900000000",
  "doctorId": "a1000000-0000-0000-0000-000000000001",
  "scheduledAt": "2026-11-21T08:00:00"
}
```

Lịch mới ở trạng thái `Pending`; sau khi worker xử lý sẽ thành `Confirmed` và có `queueNumber`. Đặt trùng khung giờ trả `409 Conflict`. Hủy lịch trả `204 No Content`.

## Cấu trúc source

```text
src/
  ClinicQueue.Appointments.Api/       Minimal API và Dockerfile
  ClinicQueue.Queue.Worker/           RabbitMQ consumer và Dockerfile
  ClinicQueue.Notification.Service/   SignalR hub/consumer và Dockerfile
  ClinicQueue.Shared/                 Entity, DbContext, event và outbox
  ClinicQueue.Client/                 Console client REST + SignalR
docker-compose.yml
.env.example
```

