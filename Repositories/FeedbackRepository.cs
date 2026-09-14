using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="IFeedbackRepository"/>
    public class FeedbackRepository : IFeedbackRepository
    {
        private readonly IMongoCollection<Feedback> _feedback;

        public FeedbackRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _feedback = database.GetCollection<Feedback>("Feedback");
        }

        public async Task CreateAsync(Feedback feedback) => await _feedback.InsertOneAsync(feedback);

        public async Task<List<Feedback>> GetRecentAsync(string type, string state, int limit = 100)
        {
            var b = Builders<Feedback>.Filter;
            var filter = b.Empty;

            if (!string.IsNullOrWhiteSpace(type)) filter &= b.Eq(f => f.Type, type);
            if (!string.IsNullOrWhiteSpace(state)) filter &= b.Eq(f => f.State, state);

            return await _feedback.Find(filter)
                .SortByDescending(f => f.CreatedAt)
                .Limit(Math.Clamp(limit, 1, 500))
                .ToListAsync();
        }

        public async Task<Feedback> GetByIdAsync(string id) =>
            await _feedback.Find(f => f.Id == id).FirstOrDefaultAsync();

        public async Task UpdateAsync(Feedback feedback)
        {
            feedback.UpdatedAt = DateTime.UtcNow;
            await _feedback.ReplaceOneAsync(f => f.Id == feedback.Id, feedback);
        }

        public async Task<long> CountByUserSinceAsync(string userId, DateTime since)
        {
            var b = Builders<Feedback>.Filter;
            return await _feedback.CountDocumentsAsync(
                b.And(b.Eq(f => f.UserId, userId), b.Gte(f => f.CreatedAt, since)));
        }
    }
}
