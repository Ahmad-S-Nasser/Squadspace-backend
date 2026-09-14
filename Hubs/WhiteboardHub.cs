using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Hubs
{
    public class WhiteboardHub : Hub
    {
        // Join a whiteboard session
        public async Task JoinWhiteboard(string whiteboardId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, whiteboardId);
            await Clients.Group(whiteboardId).SendAsync("UserJoinedWhiteboard", Context.ConnectionId);
        }

        // Leave a whiteboard session
        public async Task LeaveWhiteboard(string whiteboardId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, whiteboardId);
            await Clients.Group(whiteboardId).SendAsync("UserLeftWhiteboard", Context.ConnectionId);
        }

        // Broadcast element added
        public async Task ElementAdded(string whiteboardId, WhiteboardElement element)
        {
            await Clients.Group(whiteboardId).SendAsync("ElementAdded", element);
        }

        // Broadcast element updated
        public async Task ElementUpdated(string whiteboardId, WhiteboardElement element)
        {
            await Clients.Group(whiteboardId).SendAsync("ElementUpdated", element);
        }

        // Broadcast element deleted
        public async Task ElementDeleted(string whiteboardId, string elementId)
        {
            await Clients.Group(whiteboardId).SendAsync("ElementDeleted", elementId);
        }
    }
}