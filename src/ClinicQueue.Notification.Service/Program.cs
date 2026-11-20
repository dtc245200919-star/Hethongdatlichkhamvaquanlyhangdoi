using ClinicQueue.Notification.Service;

var builder = WebApplication.CreateBuilder(args);
var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:3000";

builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddHostedService<QueueEventConsumer>();

var app = builder.Build();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "notification-service" }));
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();
