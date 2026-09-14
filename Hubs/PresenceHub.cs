using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace RafeeqyNotes.Api.Hubs
{
    public class PresenceHub : Hub
    {
        // Thread-safe dictionary: connectionId -> userId
        private static readonly ConcurrentDictionary<string, string> OnlineUsers = new();
        // Thread-safe dictionary: userId -> status (online, away, busy, offline)
        private static readonly ConcurrentDictionary<string, string> UserStatuses = new();

        public override async Task OnConnectedAsync()
        {
            var userId = Context.GetHttpContext()?.Request.Query["userId"].FirstOrDefault()
                         ?? Context.UserIdentifier;

            if (!string.IsNullOrEmpty(userId))
            {
                OnlineUsers.TryAdd(Context.ConnectionId, userId);
                // Default to online if no status has been set
                UserStatuses.TryAdd(userId, "online");

                // Broadcast this user's current status to all clients
                var status = UserStatuses.GetValueOrDefault(userId, "online");
                await Clients.Others.SendAsync("UserStatusChanged", userId, status);
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (OnlineUsers.TryRemove(Context.ConnectionId, out var userId))
            {
                // Check if the user has other active connections
                bool hasOtherConnections = OnlineUsers.Values.Contains(userId);
                if (!hasOtherConnections)
                {
                    // User is truly offline now
                    UserStatuses.TryRemove(userId, out _);
                    await Clients.All.SendAsync("UserStatusChanged", userId, "offline");
                }
            }
            await base.OnDisconnectedAsync(exception);
        }

        // Clients will explicitly call this when changing status via UI
        public async Task UpdateStatus(string userId, string status)
        {
            // Store the status
            UserStatuses.AddOrUpdate(userId, status, (_, _) => status);
            // Broadcasts status change to all connected clients
            await Clients.All.SendAsync("UserStatusChanged", userId, status);
        }

        // Called by clients on connect to get all current user statuses
        public Task<Dictionary<string, string>> GetOnlineStatuses()
        {
            return Task.FromResult(new Dictionary<string, string>(UserStatuses));
        }
    }
}
