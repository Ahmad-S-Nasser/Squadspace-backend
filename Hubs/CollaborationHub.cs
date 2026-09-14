using Microsoft.AspNetCore.SignalR;

namespace RafeeqyNotes.Api.Hubs
{
    public class CollaborationHub : Hub
    {
        // Called when a user edits a note
        public async Task UpdateNote(string noteId, string content, string userId)
        {
            // Broadcast to all other clients editing the same note
            await Clients.Others.SendAsync("ReceiveNoteUpdate", noteId, content, userId);
        }

        // Called when a user joins a note editing session
        public async Task JoinNote(string noteId, string userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, noteId);
            await Clients.Group(noteId).SendAsync("UserJoined", noteId, userId);
        }

        // Called when a user leaves
        public async Task LeaveNote(string noteId, string userId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, noteId);
            await Clients.Group(noteId).SendAsync("UserLeft", noteId, userId);
        }
    }
}