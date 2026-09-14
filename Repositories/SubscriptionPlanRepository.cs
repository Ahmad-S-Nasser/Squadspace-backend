using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RafeeqyNotes.Api.Repositories
{
    public class SubscriptionPlanRepository : ISubscriptionPlanRepository
    {
        private readonly IMongoCollection<SubscriptionPlan> _plans;

        public SubscriptionPlanRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _plans = database.GetCollection<SubscriptionPlan>("SubscriptionPlans");
            
            // Create index on PlanId
            var indexKeys = Builders<SubscriptionPlan>.IndexKeys.Ascending(p => p.PlanId);
            var indexOptions = new CreateIndexOptions { Unique = true };
            _plans.Indexes.CreateOne(new CreateIndexModel<SubscriptionPlan>(indexKeys, indexOptions));
        }

        public async Task<List<SubscriptionPlan>> GetAllActiveAsync()
        {
            return await _plans.Find(p => p.IsActive)
                               .SortBy(p => p.SortOrder)
                               .ToListAsync();
        }

        public async Task<SubscriptionPlan?> GetByPlanIdAsync(string planId)
        {
            return await _plans.Find(p => p.PlanId == planId && p.IsActive).FirstOrDefaultAsync();
        }

        public async Task CreateAsync(SubscriptionPlan plan)
        {
            await _plans.InsertOneAsync(plan);
        }

        public async Task UpdateAsync(SubscriptionPlan plan)
        {
            plan.UpdatedAt = System.DateTime.UtcNow;
            await _plans.ReplaceOneAsync(p => p.Id == plan.Id, plan);
        }

        public async Task UpsertByPlanIdAsync(SubscriptionPlan plan)
        {
            // Deliberately matched on PlanId ALONE, not on PlanId + IsActive like
            // GetByPlanIdAsync: a deactivated plan still occupies the unique index, so
            // filtering it out here would turn the update into an insert and throw.
            var existing = await _plans.Find(p => p.PlanId == plan.PlanId).FirstOrDefaultAsync();

            if (existing == null)
            {
                await _plans.InsertOneAsync(plan);
                return;
            }

            // Keep the stored document's identity and creation time; refresh everything else.
            plan.Id = existing.Id;
            plan.CreatedAt = existing.CreatedAt;
            plan.UpdatedAt = System.DateTime.UtcNow;
            await _plans.ReplaceOneAsync(p => p.Id == existing.Id, plan);
        }

        public async Task<long> CountAsync()
        {
            return await _plans.CountDocumentsAsync(_ => true);
        }
    }
}
