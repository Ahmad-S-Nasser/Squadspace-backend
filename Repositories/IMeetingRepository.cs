using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IMeetingRepository
    {
        Task<List<Meeting>> GetAllMeetings();
        Task<List<Meeting>> GetByProjectIdAsync(string projectId);
        Task<Meeting?> GetByIdAsync(string id);
        Task CreateAsync(Meeting meeting);
        Task UpdateAsync(Meeting meeting);
        Task DeleteAsync(string id);

        /// <summary>
        /// Atomically finds one meeting starting within <paramref name="window"/> that hasn't
        /// had its reminder sent yet, and marks it sent in the same operation - so multiple API
        /// instances polling at once can't both claim it and send the reminder twice. Returns
        /// null when there is nothing due.
        /// </summary>
        Task<Meeting?> ClaimMeetingNeedingReminderAsync(DateTime now, TimeSpan window);
    }

}
