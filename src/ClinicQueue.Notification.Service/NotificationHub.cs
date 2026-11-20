using Microsoft.AspNetCore.SignalR;

namespace ClinicQueue.Notification.Service;

public sealed class NotificationHub : Hub
{
    public Task JoinDoctor(Guid doctorId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"doctor-{doctorId:N}");

    public Task LeaveDoctor(Guid doctorId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"doctor-{doctorId:N}");
}
