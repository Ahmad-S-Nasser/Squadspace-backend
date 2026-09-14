using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="IImportJobRepository"/>
    public class ImportJobRepository : IImportJobRepository
    {
        private readonly IMongoCollection<ImportJob> _jobs;

        public ImportJobRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _jobs = database.GetCollection<ImportJob>("ImportJobs");
        }

        public async Task<List<ImportJob>> GetForOrganizationAsync(string organizationId, int limit = 25)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return new List<ImportJob>();

            return await _jobs.Find(j => j.OrganizationId == organizationId)
                .SortByDescending(j => j.CreatedAt)
                .Limit(limit)
                .ToListAsync();
        }

        public async Task<ImportJob> GetByIdAsync(string id) =>
            await _jobs.Find(j => j.Id == id).FirstOrDefaultAsync();

        public async Task CreateAsync(ImportJob job) => await _jobs.InsertOneAsync(job);

        public async Task UpdateAsync(ImportJob job) =>
            await _jobs.ReplaceOneAsync(j => j.Id == job.Id, job);
    }
}
