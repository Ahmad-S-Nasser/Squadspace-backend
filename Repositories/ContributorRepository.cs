using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Repositories
{
    public class ContributorRepository : IContributorRepository
    {
        private readonly IMongoCollection<Contributor> _contributors;
        private readonly ElasticSearchService _elastic;

        public ContributorRepository(MongoDbSettings settings, ElasticSearchService elastic)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _contributors = database.GetCollection<Contributor>(settings.ContributorsCollection);

            _elastic = elastic;
        }

        public async Task<Contributor?> GetByEmailAsync(string email) =>
            await _contributors.Find(c => c.Email == email).FirstOrDefaultAsync();


        public async Task<Contributor?> GetByIdAsync(string id) =>
                await _contributors.Find(n => n.Id == id).FirstOrDefaultAsync();

        public async Task<List<Contributor>> GetAllAsync() =>
            await _contributors.Find(_ => true).ToListAsync();        
        
        public async Task<List<Contributor>> GetAllWithRoleAsync() =>
            await _contributors.Find(_ => true).ToListAsync();
        //    public async Task<List<Contributor>> GetNotesByBoardIDAsync(string board_id) =>
        //await _contributor.Find(n => n. == board_id).ToListAsync();
        public async Task CreateAsync(Contributor contributor)
        {
            if (string.IsNullOrEmpty(contributor.Id))
            {
                contributor.Id = Guid.NewGuid().ToString();
            }
            await _contributors.InsertOneAsync(contributor);

            // Index in ElasticSearch
            await _elastic.IndexContributorAsync(contributor);
        }

        public async Task UpdateAsync(Contributor contributor)
        {
            await _contributors.ReplaceOneAsync(n => n.Id == contributor.Id, contributor);

            // Re-index updated note
            await _elastic.IndexContributorAsync(contributor);
        }

        public async Task DeleteAsync(string id)
        {
            await _contributors.DeleteOneAsync(n => n.Id == id);
            await _elastic.DeleteContributorAsync(id);
        }
    }
}
