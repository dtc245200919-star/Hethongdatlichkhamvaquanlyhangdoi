using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClinicQueue.Shared;

public static class ClinicDatabase
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicDbContext>();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await db.Database.CanConnectAsync(cancellationToken))
            {
                await db.Database.EnsureCreatedAsync(cancellationToken);
                await db.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE IF NOT EXISTS "Rooms" (
                        "Id" uuid NOT NULL,
                        "Name" character varying(120) NOT NULL,
                        "Location" character varying(120) NOT NULL,
                        "ServiceStatus" character varying(20) NOT NULL,
                        CONSTRAINT "PK_Rooms" PRIMARY KEY ("Id")
                    );
                    ALTER TABLE "Doctors" ADD COLUMN IF NOT EXISTS "RoomId" uuid;
                    ALTER TABLE "Doctors" ADD COLUMN IF NOT EXISTS "ServiceStatus" character varying(20);
                    ALTER TABLE "Appointments" ADD COLUMN IF NOT EXISTS "RoomId" uuid;
                    UPDATE "Rooms"
                    SET "Name" = 'Phòng 101', "Location" = 'Tầng 1 - Khu A', "ServiceStatus" = 'Available'
                    WHERE "Id" = 'b2000000-0000-0000-0000-000000000001';
                    UPDATE "Rooms"
                    SET "Name" = 'Phòng 102', "Location" = 'Tầng 1 - Khu A', "ServiceStatus" = 'Available'
                    WHERE "Id" = 'b2000000-0000-0000-0000-000000000002';
                    UPDATE "Rooms"
                    SET "Name" = 'Phòng 201', "Location" = 'Tầng 2 - Khu B', "ServiceStatus" = 'Available'
                    WHERE "Id" = 'b2000000-0000-0000-0000-000000000003';
                    UPDATE "Doctors" SET "RoomId" =
                        CASE
                            WHEN "Id" = 'a1000000-0000-0000-0000-000000000001' THEN 'b2000000-0000-0000-0000-000000000001'::uuid
                            WHEN "Id" = 'a1000000-0000-0000-0000-000000000002' THEN 'b2000000-0000-0000-0000-000000000002'::uuid
                            WHEN "Id" = 'a1000000-0000-0000-0000-000000000003' THEN 'b2000000-0000-0000-0000-000000000003'::uuid
                        END
                    WHERE "RoomId" IS NULL;
                    UPDATE "Doctors" SET "ServiceStatus" = 'Available' WHERE "ServiceStatus" IS NULL;
                    UPDATE "Appointments" a SET "RoomId" = d."RoomId"
                    FROM "Doctors" d
                    WHERE a."DoctorId" = d."Id" AND a."RoomId" IS NULL;
                    """,
                    cancellationToken);

                if (!await db.Rooms.AnyAsync(cancellationToken))
                {
                    db.Rooms.AddRange(
                        new Room { Id = Guid.Parse("b2000000-0000-0000-0000-000000000001"), Name = "Phòng 101", Location = "Tầng 1 - Khu A" },
                        new Room { Id = Guid.Parse("b2000000-0000-0000-0000-000000000002"), Name = "Phòng 102", Location = "Tầng 1 - Khu A" },
                        new Room { Id = Guid.Parse("b2000000-0000-0000-0000-000000000003"), Name = "Phòng 201", Location = "Tầng 2 - Khu B" });
                    await db.SaveChangesAsync(cancellationToken);
                }

                if (!await db.Doctors.AnyAsync(cancellationToken))
                {
                    ClinicSeedData.Doctors[0].RoomId = Guid.Parse("b2000000-0000-0000-0000-000000000001");
                    ClinicSeedData.Doctors[1].RoomId = Guid.Parse("b2000000-0000-0000-0000-000000000002");
                    ClinicSeedData.Doctors[2].RoomId = Guid.Parse("b2000000-0000-0000-0000-000000000003");
                    db.Doctors.AddRange(ClinicSeedData.Doctors);
                    await db.SaveChangesAsync(cancellationToken);
                }

                logger.LogInformation("Clinic database is ready");
                return;
            }

            logger.LogWarning("Clinic database is unavailable; retrying in 2 seconds");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
