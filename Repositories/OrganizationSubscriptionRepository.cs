using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using System.Threading.Tasks;

namespace RafeeqyNotes.Api.Repositories
{
    public class OrganizationSubscriptionRepository : IOrganizationSubscriptionRepository
    {
        private readonly IMongoCollection<OrganizationSubscription> _subscriptions;

        public OrganizationSubscriptionRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _subscriptions = database.GetCollection<OrganizationSubscription>("OrganizationSubscriptions");
            
            // Index for fast lookups by organization
            var indexKeys = Builders<OrganizationSubscription>.IndexKeys.Ascending(s => s.OrganizationId);
            var indexOptions = new CreateIndexOptions { Unique = true }; // 1 active sub per org
            _subscriptions.Indexes.CreateOne(new CreateIndexModel<OrganizationSubscription>(indexKeys, indexOptions));
        }

        public async Task<OrganizationSubscription?> GetByOrganizationIdAsync(string organizationId)
        {
            return await _subscriptions.Find(s => s.OrganizationId == organizationId).FirstOrDefaultAsync();
        }

        public async Task<OrganizationSubscription?> GetByIdAsync(string id)
        {
            return await _subscriptions.Find(s => s.Id == id).FirstOrDefaultAsync();
        }

        public async Task CreateAsync(OrganizationSubscription subscription)
        {
            await _subscriptions.InsertOneAsync(subscription);
        }

        public async Task UpdateAsync(OrganizationSubscription subscription)
        {
            subscription.UpdatedAt = System.DateTime.UtcNow;
            await _subscriptions.ReplaceOneAsync(s => s.Id == subscription.Id, subscription);
        }
    }
}
