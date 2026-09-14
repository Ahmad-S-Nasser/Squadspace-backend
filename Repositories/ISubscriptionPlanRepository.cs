using RafeeqyNotes.Api.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RafeeqyNotes.Api.Repositories
{
    public interface ISubscriptionPlanRepository
    {
        Task<List<SubscriptionPlan>> GetAllActiveAsync();
        Task<SubscriptionPlan?> GetByPlanIdAsync(string planId);
        Task CreateAsync(SubscriptionPlan plan);
        Task UpdateAsync(SubscriptionPlan plan);
        Task<long> CountAsync();

        /// <summary>
        /// Inserts a plan, or updates the existing one with the same PlanId.
        /// </summary>
        /// <remarks>
        /// The seeder previously only ran when the collection was completely empty, so a new
        /// plan added to the seed list never reached any existing deployment. A plain re-run
        /// was not an option either: PlanId carries a unique index, so re-inserting the four
        /// existing plans throws a duplicate-key error that aborts the loop part-way.
        /// </remarks>
        Task UpsertByPlanIdAsync(SubscriptionPlan plan);
    }
}
