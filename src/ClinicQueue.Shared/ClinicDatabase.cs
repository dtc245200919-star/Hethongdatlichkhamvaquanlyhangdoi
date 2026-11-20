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

                if (!await db.Doctors.AnyAsync(cancellationToken))
                {
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
