using Microsoft.EntityFrameworkCore;

namespace ClinicQueue.Shared;

public sealed class ClinicDbContext(DbContextOptions<ClinicDbContext> options) : DbContext(options)
{
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<QueueCounter> QueueCounters => Set<QueueCounter>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Doctor>(entity =>
        {
            entity.ToTable("Doctors");
            entity.HasKey(doctor => doctor.Id);
            entity.Property(doctor => doctor.Name).HasMaxLength(120).IsRequired();
            entity.Property(doctor => doctor.Specialty).HasMaxLength(120).IsRequired();
            entity.Property(doctor => doctor.ServiceStatus).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(doctor => doctor.Room)
                .WithMany()
                .HasForeignKey(doctor => doctor.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Room>(entity =>
        {
            entity.ToTable("Rooms");
            entity.HasKey(room => room.Id);
            entity.Property(room => room.Name).HasMaxLength(120).IsRequired();
            entity.Property(room => room.Location).HasMaxLength(120).IsRequired();
            entity.Property(room => room.ServiceStatus).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Appointment>(entity =>
        {
            entity.ToTable("Appointments");
            entity.HasKey(appointment => appointment.Id);
            entity.Property(appointment => appointment.PatientName).HasMaxLength(120).IsRequired();
            entity.Property(appointment => appointment.PatientPhone).HasMaxLength(40).IsRequired();
            entity.Property(appointment => appointment.Status).HasConversion<string>().HasMaxLength(20);
            entity.HasOne(appointment => appointment.Room)
                .WithMany()
                .HasForeignKey(appointment => appointment.RoomId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(appointment => new { appointment.DoctorId, appointment.ScheduledAt })
                .IsUnique()
                .HasFilter("\"Status\" <> 'Cancelled'");
            entity.HasIndex(appointment => new { appointment.DoctorId, appointment.ScheduledAt, appointment.Status });
            entity.HasOne(appointment => appointment.Doctor)
                .WithMany()
                .HasForeignKey(appointment => appointment.DoctorId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QueueCounter>(entity =>
        {
            entity.ToTable("QueueCounters");
            entity.HasKey(counter => new { counter.DoctorId, counter.QueueDate });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(message => message.Id);
            entity.Property(message => message.EventType).HasMaxLength(80).IsRequired();
            entity.Property(message => message.Payload).IsRequired();
            entity.HasIndex(message => new { message.EventType, message.PublishedAt, message.CreatedAt });
        });
    }
}
