using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;
using Microsoft.AspNetCore.Mvc;

namespace RafeeqyNotes.Api.Repositories
{
    public class MeetingRepository : IMeetingRepository
    {
        private readonly IMongoCollection<Meeting> _meetings;

        public MeetingRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _meetings = database.GetCollection<Meeting>(settings.MeetingsCollection);
        }

        public async Task<List<Meeting>> GetByProjectIdAsync(string projectId) =>
            await _meetings.Find(m => m.ProjectId == projectId).ToListAsync();

        public async Task<List<Meeting>> GetAllMeetings() =>
    await _meetings.Find(_ => true).ToListAsync();
        public async Task<Meeting?> GetByIdAsync(string id) =>
            await _meetings.Find(m => m.Id == id).FirstOrDefaultAsync();

        public async Task CreateAsync(Meeting meeting) =>
            await _meetings.InsertOneAsync(meeting);

        public async Task UpdateAsync(Meeting meeting) =>
            await _meetings.ReplaceOneAsync(m => m.Id == meeting.Id, meeting);

        public async Task DeleteAsync(string id) =>
            await _meetings.DeleteOneAsync(m => m.Id == id);

        public async Task<Meeting?> ClaimMeetingNeedingReminderAsync(DateTime now, TimeSpan window)
        {
            var b = Builders<Meeting>.Filter;

            var due = b.And(
                b.Gt(m => m.ScheduledAt, now),
                b.Lte(m => m.ScheduledAt, now.Add(window)),
                b.Eq(m => m.ReminderSentAt, null));

            var claim = Builders<Meeting>.Update.Set(m => m.ReminderSentAt, now);

            return await _meetings.FindOneAndUpdateAsync(due, claim,
                new FindOneAndUpdateOptions<Meeting> { ReturnDocument = ReturnDocument.After });
        }
    }
}