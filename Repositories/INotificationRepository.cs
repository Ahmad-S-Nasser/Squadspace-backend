using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface INotificationRepository
    {
        Task<NotificationResponse> SendEmailNotificationAsync(NotificationRequest request);
        Task<List<Notification>> GetUserNotificationsAsync(string userId);
        Task CreateUserNotificationAsync(Notification notification);

        // Every mutation is scoped by userId as well as id, so one user can never mark or
        // delete another user's notifications by guessing an id.

        /// <summary>Marks one notification read. False when it doesn't exist for this user.</summary>
        Task<bool> MarkAsReadAsync(string id, string userId);

        /// <summary>Marks all of the user's unread notifications read. Returns how many changed.</summary>
        Task<long> MarkAllAsReadAsync(string userId);

        /// <summary>Deletes one notification. False when it doesn't exist for this user.</summary>
        Task<bool> DeleteUserNotificationAsync(string id, string userId);
        Task<NotificationPreferences?> GetPreferencesAsync(string userId);
        Task UpdatePreferencesAsync(NotificationPreferences preferences);
    }
}
