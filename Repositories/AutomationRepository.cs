using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="IAutomationRepository"/>
    public class AutomationRepository : IAutomationRepository
    {
        private readonly IMongoCollection<AutomationRule> _rules;
        private readonly IMongoCollection<AutomationRun> _runs;

        public AutomationRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _rules = database.GetCollection<AutomationRule>("AutomationRules");
            _runs = database.GetCollection<AutomationRun>("AutomationRuns");
        }

        public async Task<List<AutomationRule>> GetForOrganizationAsync(string organizationId)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return new List<AutomationRule>();

            return await _rules.Find(r => r.OrganizationId == organizationId)
                .SortByDescending(r => r.UpdatedAt)
                .ToListAsync();
        }

        public async Task<AutomationRule> GetByIdAsync(string id) =>
            await _rules.Find(r => r.Id == id).FirstOrDefaultAsync();

        public async Task UpsertAsync(AutomationRule rule)
        {
            rule.UpdatedAt = DateTime.UtcNow;
            await _rules.ReplaceOneAsync(r => r.Id == rule.Id, rule, new ReplaceOptions { IsUpsert = true });
        }

        public async Task DeleteAsync(string id) => await _rules.DeleteOneAsync(r => r.Id == id);

        public async Task<List<AutomationRule>> GetEnabledByTriggerAsync(string organizationId, string trigger)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return new List<AutomationRule>();

            return await _rules.Find(r =>
                    r.OrganizationId == organizationId && r.Enabled && r.Trigger == trigger)
                .ToListAsync();
        }

        public async Task<AutomationRule> ClaimDueRuleAsync(DateTime now, TimeSpan leaseFor)
        {
            var b = Builders<AutomationRule>.Filter;

            var due = b.And(
                b.Eq(r => r.Enabled, true),
                b.Eq(r => r.Trigger, AutomationTriggers.Schedule),
                b.Lte(r => r.NextRunAt, now),
                // Not currently leased by another instance, or the lease has lapsed because that
                // instance died mid-run.
                b.Or(
                    b.Eq(r => r.LockedUntil, null),
                    b.Lt(r => r.LockedUntil, now)));

            var claim = Builders<AutomationRule>.Update
                .Set(r => r.LockedUntil, now.Add(leaseFor));

            return await _rules.FindOneAndUpdateAsync(due, claim,
                new FindOneAndUpdateOptions<AutomationRule> { ReturnDocument = ReturnDocument.After });
        }

        public async Task RecordRunAsync(AutomationRun run) => await _runs.InsertOneAsync(run);

        public async Task<List<AutomationRun>> GetRunsAsync(string organizationId, string ruleId, int limit = 50)
        {
            var b = Builders<AutomationRun>.Filter;
            var filter = b.Eq(r => r.OrganizationId, organizationId);

            if (!string.IsNullOrWhiteSpace(ruleId)) filter &= b.Eq(r => r.RuleId, ruleId);

            return await _runs.Find(filter)
                .SortByDescending(r => r.StartedAt)
                .Limit(Math.Clamp(limit, 1, 200))
                .ToListAsync();
        }

        /// <remarks>
        /// Counts only runs that actually did something. A quota refusal or a skipped condition
        /// must not consume the allowance it was measured against - otherwise a rule whose
        /// condition never matches quietly burns a customer's monthly budget.
        /// </remarks>
        public async Task<long> CountRunsSinceAsync(string organizationId, DateTime since)
        {
            var b = Builders<AutomationRun>.Filter;

            return await _runs.CountDocumentsAsync(b.And(
                b.Eq(r => r.OrganizationId, organizationId),
                b.Gte(r => r.StartedAt, since),
                b.In(r => r.State, new[] { AutomationRunStates.Succeeded, AutomationRunStates.Failed })));
        }
    }
}
