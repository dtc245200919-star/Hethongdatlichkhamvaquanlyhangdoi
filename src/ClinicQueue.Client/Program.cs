using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;

var apiUrl = Environment.GetEnvironmentVariable("APPOINTMENT_API_URL") ?? "http://localhost:8080";
var hubUrl = Environment.GetEnvironmentVariable("NOTIFICATION_HUB_URL") ?? "http://localhost:8082/hubs/notifications";
using var httpClient = new HttpClient { BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/") };

var doctors = await httpClient.GetFromJsonAsync<List<DoctorDto>>("doctors")
    ?? throw new InvalidOperationException("The API returned an empty doctor list.");
Console.WriteLine($"Đã kết nối API {httpClient.BaseAddress}");
PrintDoctors(doctors);

await using var hubConnection = new HubConnectionBuilder()
    .WithUrl(hubUrl)
    .WithAutomaticReconnect()
    .Build();

hubConnection.On<QueueChangedEvent>("QueueChanged", message =>
{
    Console.WriteLine(
        $"\n[SignalR] {message.PatientName} | {message.Status} | " +
        $"{message.QueueNumber?.ToString(CultureInfo.InvariantCulture) ?? "đang xử lý"} | " +
        $"bác sĩ {message.DoctorId}");
});
hubConnection.Reconnecting += exception =>
{
    Console.WriteLine($"[SignalR] Mất kết nối, đang thử lại: {exception?.Message ?? "đang kết nối"}");
    return Task.CompletedTask;
};
hubConnection.Reconnected += async _ =>
{
    foreach (var doctor in doctors)
    {
        await hubConnection.InvokeAsync("JoinDoctor", doctor.Id);
    }

    Console.WriteLine("[SignalR] Đã kết nối lại và đăng ký nhận cập nhật hàng đợi.");
};

await hubConnection.StartAsync();
foreach (var doctor in doctors)
{
    await hubConnection.InvokeAsync("JoinDoctor", doctor.Id);
}
Console.WriteLine($"Đã kết nối SignalR {hubUrl}");

while (true)
{
    Console.WriteLine(
        "\n1. Danh sách bác sĩ\n" +
        "2. Xem khung giờ trống\n" +
        "3. Đặt lịch khám\n" +
        "4. Tra cứu lịch khám\n" +
        "5. Hủy lịch khám\n" +
        "6. Xem hàng đợi\n" +
        "0. Thoát");
    Console.Write("Chọn chức năng: ");
    var command = Console.ReadLine();

    switch (command)
    {
        case "1":
            PrintDoctors(doctors);
            break;
        case "2":
            await ShowSlotsAsync(httpClient, doctors);
            break;
        case "3":
            await BookAppointmentAsync(httpClient, doctors);
            break;
        case "4":
            await FindAppointmentAsync(httpClient);
            break;
        case "5":
            await CancelAppointmentAsync(httpClient);
            break;
        case "6":
            await ShowQueueAsync(httpClient, doctors);
            break;
        case "0":
        case null:
            return;
        default:
            Console.WriteLine("Lựa chọn không hợp lệ.");
            break;
    }
}

static void PrintDoctors(IEnumerable<DoctorDto> doctors)
{
    Console.WriteLine("\nDanh sách bác sĩ:");
    foreach (var doctor in doctors)
    {
        Console.WriteLine($"- {doctor.Id} | {doctor.Name} | {doctor.Specialty} | {doctor.OpensAt}-{doctor.ClosesAt}");
    }
}

static async Task ShowSlotsAsync(HttpClient client, IReadOnlyList<DoctorDto> doctors)
{
    var doctor = SelectDoctor(doctors);
    if (doctor is null || !TryReadDate(out var date))
    {
        return;
    }

    var slots = await client.GetFromJsonAsync<List<DateTime>>(
        $"doctors/{doctor.Id}/slots?date={date:yyyy-MM-dd}") ?? [];
    if (slots.Count == 0)
    {
        Console.WriteLine("Không còn khung giờ trống trong ngày đã chọn.");
        return;
    }

    for (var index = 0; index < slots.Count; index++)
    {
        Console.WriteLine($"{index + 1}. {slots[index]:yyyy-MM-dd HH:mm}");
    }
}

static async Task BookAppointmentAsync(HttpClient client, IReadOnlyList<DoctorDto> doctors)
{
    var doctor = SelectDoctor(doctors);
    if (doctor is null || !TryReadDate(out var date))
    {
        return;
    }

    var slots = await client.GetFromJsonAsync<List<DateTime>>(
        $"doctors/{doctor.Id}/slots?date={date:yyyy-MM-dd}") ?? [];
    if (slots.Count == 0)
    {
        Console.WriteLine("Không còn khung giờ trống trong ngày đã chọn.");
        return;
    }

    for (var index = 0; index < slots.Count; index++)
    {
        Console.WriteLine($"{index + 1}. {slots[index]:yyyy-MM-dd HH:mm}");
    }

    Console.Write("Chọn số thứ tự khung giờ: ");
    if (!int.TryParse(Console.ReadLine(), out var selection) || selection < 1 || selection > slots.Count)
    {
        Console.WriteLine("Số thứ tự khung giờ không hợp lệ.");
        return;
    }

    Console.Write("Họ tên bệnh nhân: ");
    var patientName = Console.ReadLine()?.Trim();
    Console.Write("Số điện thoại: ");
    var patientPhone = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(patientName) || string.IsNullOrWhiteSpace(patientPhone))
    {
        Console.WriteLine("Họ tên và số điện thoại không được để trống.");
        return;
    }

    using var response = await client.PostAsJsonAsync("appointments", new
    {
        patientName,
        patientPhone,
        doctorId = doctor.Id,
        scheduledAt = slots[selection - 1]
    });
    await PrintResponseAsync(response);
}

static async Task FindAppointmentAsync(HttpClient client)
{
    Console.Write("Mã lịch khám: ");
    var id = Console.ReadLine();
    if (Guid.TryParse(id, out var appointmentId))
    {
        using var response = await client.GetAsync($"appointments/{appointmentId}");
        await PrintResponseAsync(response);
    }
    else
    {
        Console.WriteLine("Mã lịch khám không hợp lệ.");
    }
}

static async Task CancelAppointmentAsync(HttpClient client)
{
    Console.Write("Mã lịch khám cần hủy: ");
    var id = Console.ReadLine();
    if (Guid.TryParse(id, out var appointmentId))
    {
        using var response = await client.DeleteAsync($"appointments/{appointmentId}");
        await PrintResponseAsync(response);
    }
    else
    {
        Console.WriteLine("Mã lịch khám không hợp lệ.");
    }
}

static async Task ShowQueueAsync(HttpClient client, IReadOnlyList<DoctorDto> doctors)
{
    var doctor = SelectDoctor(doctors);
    if (doctor is null || !TryReadDate(out var date))
    {
        return;
    }

    using var response = await client.GetAsync($"queues/{doctor.Id}?date={date:yyyy-MM-dd}");
    await PrintResponseAsync(response);
}

static DoctorDto? SelectDoctor(IReadOnlyList<DoctorDto> doctors)
{
    Console.Write("Mã bác sĩ: ");
    var input = Console.ReadLine();
    if (Guid.TryParse(input, out var doctorId))
    {
        var doctor = doctors.SingleOrDefault(item => item.Id == doctorId);
        if (doctor is not null)
        {
            return doctor;
        }
    }

    Console.WriteLine("Không tìm thấy bác sĩ.");
    return null;
}

static bool TryReadDate(out DateOnly date)
{
    Console.Write("Ngày khám (yyyy-MM-dd): ");
    if (DateOnly.TryParseExact(Console.ReadLine(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
    {
        return true;
    }

    Console.WriteLine("Ngày không hợp lệ.");
    return false;
}

static async Task PrintResponseAsync(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Lỗi {(int)response.StatusCode}: {body}");
        return;
    }

    Console.WriteLine(string.IsNullOrWhiteSpace(body) ? $"Thành công: {(int)response.StatusCode}" : body);
}

sealed record DoctorDto(Guid Id, string Name, string Specialty, string OpensAt, string ClosesAt);

sealed record QueueChangedEvent(
    Guid AppointmentId,
    Guid DoctorId,
    DateOnly QueueDate,
    int? QueueNumber,
    string PatientName,
    string Status);
