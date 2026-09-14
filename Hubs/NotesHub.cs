using Microsoft.AspNetCore.SignalR;

namespace RafeeqyNotes.Api.Hubs
{
    public class NotesHub : Hub
    {
        public async Task UpdateNote(string noteId, string action, object payload)
        {
            await Clients.Group(noteId).SendAsync("ReceiveNoteUpdate", action, payload);
        }

        public async Task JoinNote(string noteId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, noteId);
            await Clients.Group(noteId).SendAsync("UserJoinedNote", Context.ConnectionId);
        }

        public async Task LeaveNote(string noteId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, noteId);
            await Clients.Group(noteId).SendAsync("UserLeftNote", Context.ConnectionId);
        }
    }
}