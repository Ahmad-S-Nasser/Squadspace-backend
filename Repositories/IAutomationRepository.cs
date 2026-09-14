using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IAutomationRepository
    {
        Task<List<AutomationRule>> GetForOrganizationAsync(string organizationId);
        Task<AutomationRule> GetByIdAsync(string id);
        Task UpsertAsync(AutomationRule rule);
        Task DeleteAsync(string id);

        /// <summary>Enabled event-driven rules matching a trigger, for one organization.</summary>
        Task<List<AutomationRule>> GetEnabledByTriggerAsync(string organizationId, string trigger);

        /// <summary>
        /// Atomically claims one due scheduled rule, or returns null.
        /// </summary>
        /// <remarks>
        /// The claim IS the concurrency control. Several app instances poll the same collection,
        /// so selecting due rules and then updating them would let every instance run the same
        /// rule - one report, four copies. FindOneAndUpdate does both in a single round trip, and
        /// only one caller can win.
        /// </remarks>
        Task<AutomationRule> ClaimDueRuleAsync(DateTime now, TimeSpan leaseFor);

        Task RecordRunAsync(AutomationRun run);
        Task<List<AutomationRun>> GetRunsAsync(string organizationId, string ruleId, int limit = 50);

        /// <summary>Runs an organization has used since <paramref name="since"/>. The meter.</summary>
        Task<long> CountRunsSinceAsync(string organizationId, DateTime since);
    }
}
