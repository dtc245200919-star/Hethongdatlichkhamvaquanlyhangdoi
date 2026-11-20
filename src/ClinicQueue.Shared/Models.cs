namespace ClinicQueue.Shared;

public enum AppointmentStatus
{
    Pending,
    Confirmed,
    Cancelled
}

public sealed class Doctor
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Specialty { get; set; } = "";
    public TimeOnly OpensAt { get; set; }
    public TimeOnly ClosesAt { get; set; }
}

public sealed class Appointment
{
    public Guid Id { get; set; }
    public string PatientName { get; set; } = "";
    public string PatientPhone { get; set; } = "";
    public Guid DoctorId { get; set; }
    public Doctor? Doctor { get; set; }
    public DateTime ScheduledAt { get; set; }
    public AppointmentStatus Status { get; set; }
    public int? QueueNumber { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class QueueCounter
{
    public Guid DoctorId { get; set; }
    public DateOnly QueueDate { get; set; }
    public int LastNumber { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public sealed record AppointmentRequestedEvent(Guid AppointmentId);

public sealed record QueueChangedEvent(
    Guid AppointmentId,
    Guid DoctorId,
    DateOnly QueueDate,
    int? QueueNumber,
    string PatientName,
    string Status);

public sealed record DoctorResponse(Guid Id, string Name, string Specialty, string OpensAt, string ClosesAt);

public sealed record AppointmentResponse(
    Guid Id,
    string PatientName,
    string PatientPhone,
    Guid DoctorId,
    string DoctorName,
    DateTime ScheduledAt,
    string Status,
    int? QueueNumber,
    DateTime CreatedAt);

public sealed record CreateAppointmentRequest(
    string PatientName,
    string PatientPhone,
    Guid DoctorId,
    DateTime ScheduledAt);

public static class ClinicSeedData
{
    public static readonly IReadOnlyList<Doctor> Doctors =
    [
        new()
        {
            Id = Guid.Parse("a1000000-0000-0000-0000-000000000001"),
            Name = "Nguyễn Minh Anh",
            Specialty = "Nội tổng quát",
            OpensAt = new TimeOnly(8, 0),
            ClosesAt = new TimeOnly(16, 0)
        },
        new()
        {
            Id = Guid.Parse("a1000000-0000-0000-0000-000000000002"),
            Name = "Trần Hoàng Nam",
            Specialty = "Nhi khoa",
            OpensAt = new TimeOnly(8, 30),
            ClosesAt = new TimeOnly(16, 30)
        },
        new()
        {
            Id = Guid.Parse("a1000000-0000-0000-0000-000000000003"),
            Name = "Lê Thu Hà",
            Specialty = "Tim mạch",
            OpensAt = new TimeOnly(9, 0),
            ClosesAt = new TimeOnly(17, 0)
        }
    ];
}
