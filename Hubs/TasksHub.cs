using Microsoft.AspNetCore.SignalR;

namespace RafeeqyNotes.Api.Hubs
{
    public class TasksHub : Hub
    {
        public async Task UpdateTask(string taskId, string action, object payload)
        {
            await Clients.Group(taskId).SendAsync("ReceiveTaskUpdate", action, payload);
        }

        public async Task JoinTask(string taskId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, taskId);
            await Clients.Group(taskId).SendAsync("UserJoinedTask", Context.ConnectionId);
        }

        public async Task LeaveTask(string taskId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, taskId);
            await Clients.Group(taskId).SendAsync("UserLeftTask", Context.ConnectionId);
        }
    }
}