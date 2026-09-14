using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        // Broadcast new message
        public async Task SendMessage(string chatId, ChatMessage message)
        {
            await Clients.Group(chatId).SendAsync("ReceiveMessage", message);
        }

        // Broadcast edited message
        public async Task EditMessage(string chatId, ChatMessage message)
        {
            await Clients.Group(chatId).SendAsync("MessageEdited", message);
        }

        // Broadcast deleted message
        public async Task DeleteMessage(string chatId, string messageId)
        {
            await Clients.Group(chatId).SendAsync("MessageDeleted", messageId);
        }

        // Broadcast reaction
        public async Task AddReaction(string chatId, string messageId, ChatReaction reaction)
        {
            await Clients.Group(chatId).SendAsync("ReactionAdded", messageId, reaction);
        }

        // Join chat room
        public async Task JoinChat(string chatId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, chatId);
            await Clients.Group(chatId).SendAsync("UserJoinedChat", Context.ConnectionId);
        }

        // Leave chat room
        public async Task LeaveChat(string chatId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, chatId);
            await Clients.Group(chatId).SendAsync("UserLeftChat", Context.ConnectionId);
        }
    }
}