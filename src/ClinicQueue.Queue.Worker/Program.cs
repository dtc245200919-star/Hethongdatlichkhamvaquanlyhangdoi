using ClinicQueue.Queue.Worker;
using ClinicQueue.Shared;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
var connectionString = builder.Configuration.GetConnectionString("ClinicDb")
    ?? throw new InvalidOperationException("ConnectionStrings:ClinicDb is required.");

builder.Services.AddDbContext<ClinicDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<RabbitMqPublisher>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService(serviceProvider => new OutboxDispatcher(
    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
    serviceProvider.GetRequiredService<RabbitMqPublisher>(),
    serviceProvider.GetRequiredService<ILogger<OutboxDispatcher>>(),
    EventTypes.QueueChanged));

var host = builder.Build();
host.Run();
