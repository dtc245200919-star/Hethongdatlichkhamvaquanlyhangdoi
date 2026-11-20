using System.Text.Json;
using ClinicQueue.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("ClinicDb")
    ?? throw new InvalidOperationException("ConnectionStrings:ClinicDb is required.");
var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:3000";

builder.Services.AddDbContext<ClinicDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddHostedService(serviceProvider => new OutboxDispatcher(
    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
    serviceProvider.GetRequiredService<RabbitMqPublisher>(),
    serviceProvider.GetRequiredService<ILogger<OutboxDispatcher>>(),
    EventTypes.AppointmentRequested));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
await ClinicDatabase.InitializeAsync(app.Services, app.Logger, app.Lifetime.ApplicationStopping);

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", async (ClinicDbContext db, CancellationToken cancellationToken) =>
    await db.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "healthy", service = "appointments-api" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/doctors", async (ClinicDbContext db, CancellationToken cancellationToken) =>
{
    var doctors = await db.Doctors.AsNoTracking().OrderBy(doctor => doctor.Name).ToListAsync(cancellationToken);
    return Results.Ok(doctors.Select(ToDoctorResponse));
});

app.MapGet("/doctors/{doctorId:guid}/slots", async (
    Guid doctorId,
    DateOnly? date,
    ClinicDbContext db,
    CancellationToken cancellationToken) =>
{
    var doctor = await db.Doctors.AsNoTracking().SingleOrDefaultAsync(item => item.Id == doctorId, cancellationToken);
    if (doctor is null)
    {
        return Results.NotFound(new { error = "Không tìm thấy bác sĩ." });
    }

    var requestedDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    var today = DateOnly.FromDateTime(DateTime.Today);
    if (requestedDate < today || requestedDate > today.AddDays(30))
    {
        return Results.BadRequest(new { error = "Ngày khám phải nằm trong 30 ngày tới." });
    }

    var start = requestedDate.ToDateTime(TimeOnly.MinValue);
    var end = start.AddDays(1);
    var bookedSlots = await db.Appointments.AsNoTracking()
        .Where(appointment =>
            appointment.DoctorId == doctorId &&
            appointment.ScheduledAt >= start &&
            appointment.ScheduledAt < end &&
            appointment.Status != AppointmentStatus.Cancelled)
        .Select(appointment => appointment.ScheduledAt)
        .ToListAsync(cancellationToken);

    var booked = bookedSlots.ToHashSet();
    var slots = new List<DateTime>();
    for (var slot = requestedDate.ToDateTime(doctor.OpensAt);
         slot.AddMinutes(30) <= requestedDate.ToDateTime(doctor.ClosesAt);
         slot = slot.AddMinutes(30))
    {
        if (slot > DateTime.Now && !booked.Contains(slot))
        {
            slots.Add(slot);
        }
    }

    return Results.Ok(slots);
});

app.MapPost("/appointments", async (
    CreateAppointmentRequest request,
    ClinicDbContext db,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var patientName = request.PatientName?.Trim();
    var patientPhone = request.PatientPhone?.Trim();
    if (string.IsNullOrWhiteSpace(patientName) || patientName.Length > 120 ||
        string.IsNullOrWhiteSpace(patientPhone) || patientPhone.Length > 40)
    {
        return Results.BadRequest(new { error = "Họ tên (tối đa 120 ký tự) và số điện thoại (tối đa 40 ký tự) là bắt buộc." });
    }

    var doctor = await db.Doctors.SingleOrDefaultAsync(item => item.Id == request.DoctorId, cancellationToken);
    if (doctor is null)
    {
        return Results.NotFound(new { error = "Không tìm thấy bác sĩ." });
    }

    var requestedDate = DateOnly.FromDateTime(request.ScheduledAt);
    var today = DateOnly.FromDateTime(DateTime.Today);
    var time = TimeOnly.FromDateTime(request.ScheduledAt);
    var minutesFromOpening = (time.ToTimeSpan() - doctor.OpensAt.ToTimeSpan()).TotalMinutes;
    if (requestedDate < today || requestedDate > today.AddDays(30) ||
        time < doctor.OpensAt || time >= doctor.ClosesAt ||
        minutesFromOpening % 30 != 0 || request.ScheduledAt <= DateTime.Now)
    {
        return Results.BadRequest(new { error = "Khung giờ không hợp lệ hoặc đã qua. Hãy chọn khung giờ còn trống trong 30 ngày tới." });
    }

    var appointment = new Appointment
    {
        Id = Guid.NewGuid(),
        PatientName = patientName,
        PatientPhone = patientPhone,
        DoctorId = doctor.Id,
        ScheduledAt = DateTime.SpecifyKind(request.ScheduledAt, DateTimeKind.Unspecified),
        Status = AppointmentStatus.Pending,
        CreatedAt = DateTime.UtcNow
    };

    var requestedEvent = new AppointmentRequestedEvent(appointment.Id);
    db.Appointments.Add(appointment);
    db.OutboxMessages.Add(new OutboxMessage
    {
        Id = Guid.NewGuid(),
        EventType = EventTypes.AppointmentRequested,
        Payload = JsonSerializer.Serialize(requestedEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        CreatedAt = DateTime.UtcNow
    });

    try
    {
        await db.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException exception) when (
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_Appointments_DoctorId_ScheduledAt"
        })
    {
        logger.LogInformation(exception, "Appointment slot collision for doctor {DoctorId} at {ScheduledAt}",
            request.DoctorId, request.ScheduledAt);
        return Results.Conflict(new { error = "Khung giờ vừa được người khác đặt. Vui lòng chọn khung giờ khác." });
    }

    logger.LogInformation("Appointment {AppointmentId} accepted for doctor {DoctorId}", appointment.Id, doctor.Id);
    return Results.Accepted($"/appointments/{appointment.Id}", ToAppointmentResponse(appointment, doctor));
});

app.MapGet("/appointments/{appointmentId:guid}", async (
    Guid appointmentId,
    ClinicDbContext db,
    CancellationToken cancellationToken) =>
{
    var appointment = await db.Appointments.AsNoTracking()
        .Include(item => item.Doctor)
        .SingleOrDefaultAsync(item => item.Id == appointmentId, cancellationToken);
    return appointment is null
        ? Results.NotFound(new { error = "Không tìm thấy lịch khám." })
        : Results.Ok(ToAppointmentResponse(appointment, appointment.Doctor!));
});

app.MapDelete("/appointments/{appointmentId:guid}", async (
    Guid appointmentId,
    ClinicDbContext db,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var appointment = await db.Appointments
        .Include(item => item.Doctor)
        .SingleOrDefaultAsync(item => item.Id == appointmentId, cancellationToken);
    if (appointment is null)
    {
        return Results.NotFound(new { error = "Không tìm thấy lịch khám." });
    }

    if (appointment.Status == AppointmentStatus.Cancelled)
    {
        return Results.NoContent();
    }

    appointment.Status = AppointmentStatus.Cancelled;
    var changedEvent = new QueueChangedEvent(
        appointment.Id,
        appointment.DoctorId,
        DateOnly.FromDateTime(appointment.ScheduledAt),
        appointment.QueueNumber,
        appointment.PatientName,
        appointment.Status.ToString());
    db.OutboxMessages.Add(new OutboxMessage
    {
        Id = Guid.NewGuid(),
        EventType = EventTypes.QueueChanged,
        Payload = JsonSerializer.Serialize(changedEvent, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        CreatedAt = DateTime.UtcNow
    });
    await db.SaveChangesAsync(cancellationToken);
    logger.LogInformation("Appointment {AppointmentId} cancelled", appointmentId);
    return Results.NoContent();
});

app.MapGet("/queues/{doctorId:guid}", async (
    Guid doctorId,
    DateOnly? date,
    ClinicDbContext db,
    CancellationToken cancellationToken) =>
{
    var doctor = await db.Doctors.AsNoTracking().SingleOrDefaultAsync(item => item.Id == doctorId, cancellationToken);
    if (doctor is null)
    {
        return Results.NotFound(new { error = "Không tìm thấy bác sĩ." });
    }

    var queueDate = date ?? DateOnly.FromDateTime(DateTime.Today);
    var start = queueDate.ToDateTime(TimeOnly.MinValue);
    var end = start.AddDays(1);
    var appointments = await db.Appointments.AsNoTracking()
        .Where(appointment =>
            appointment.DoctorId == doctorId &&
            appointment.ScheduledAt >= start &&
            appointment.ScheduledAt < end &&
            appointment.Status != AppointmentStatus.Cancelled)
        .OrderBy(appointment => appointment.QueueNumber == null)
        .ThenBy(appointment => appointment.QueueNumber)
        .ThenBy(appointment => appointment.ScheduledAt)
        .ToListAsync(cancellationToken);

    return Results.Ok(new
    {
        doctor = ToDoctorResponse(doctor),
        date = queueDate,
        appointments = appointments.Select(appointment => new
        {
            appointment.Id,
            appointment.PatientName,
            appointment.ScheduledAt,
            status = appointment.Status.ToString(),
            appointment.QueueNumber
        })
    });
});

app.Run();

static DoctorResponse ToDoctorResponse(Doctor doctor) =>
    new(doctor.Id, doctor.Name, doctor.Specialty, doctor.OpensAt.ToString("HH:mm"), doctor.ClosesAt.ToString("HH:mm"));

static AppointmentResponse ToAppointmentResponse(Appointment appointment, Doctor doctor) =>
    new(
        appointment.Id,
        appointment.PatientName,
        appointment.PatientPhone,
        appointment.DoctorId,
        doctor.Name,
        appointment.ScheduledAt,
        appointment.Status.ToString(),
        appointment.QueueNumber,
        appointment.CreatedAt);
