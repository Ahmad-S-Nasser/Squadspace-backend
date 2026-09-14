using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IFeedbackRepository
    {
        Task CreateAsync(Feedback feedback);
        Task<List<Feedback>> GetRecentAsync(string type, string state, int limit = 100);
        Task<Feedback> GetByIdAsync(string id);
        Task UpdateAsync(Feedback feedback);

        /// <summary>How many this user has sent since <paramref name="since"/>. Anti-spam.</summary>
        Task<long> CountByUserSinceAsync(string userId, DateTime since);
    }
}
