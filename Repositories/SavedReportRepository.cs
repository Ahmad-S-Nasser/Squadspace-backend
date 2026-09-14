using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="ISavedReportRepository"/>
    public class SavedReportRepository : ISavedReportRepository
    {
        private readonly IMongoCollection<SavedReport> _reports;

        // Indexes are built by SchemaIndexes after the app is built, not here. See
        // MigrationRunner for why a constructor is the wrong place for them in this codebase.
        public SavedReportRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _reports = database.GetCollection<SavedReport>("SavedReports");
        }

        /// <remarks>
        /// The organization predicate is separate from and outside the visibility predicate, so
        /// "shared with the organization" can never widen past the organization itself.
        /// </remarks>
        public async Task<List<SavedReport>> GetVisibleAsync(string organizationId, string userId)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return new List<SavedReport>();

            var b = Builders<SavedReport>.Filter;
            var filter = b.Eq(r => r.OrganizationId, organizationId)
                         & (b.Eq(r => r.OwnerId, userId) | b.Eq(r => r.SharedWithOrganization, true));

            return await _reports.Find(filter)
                .SortBy(r => r.SortOrder)
                .ThenByDescending(r => r.UpdatedAt)
                .ToListAsync();
        }

        public async Task<SavedReport> GetByIdAsync(string id) =>
            await _reports.Find(r => r.Id == id).FirstOrDefaultAsync();

        public async Task CreateAsync(SavedReport report)
        {
            if (string.IsNullOrWhiteSpace(report.Id)) report.Id = Guid.NewGuid().ToString();
            report.CreatedAt = DateTime.UtcNow;
            report.UpdatedAt = report.CreatedAt;
            await _reports.InsertOneAsync(report);
        }

        public async Task UpdateAsync(SavedReport report)
        {
            report.UpdatedAt = DateTime.UtcNow;
            await _reports.ReplaceOneAsync(r => r.Id == report.Id, report);
        }

        public async Task DeleteAsync(string id) =>
            await _reports.DeleteOneAsync(r => r.Id == id);
    }
}
