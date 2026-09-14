using RafeeqyNotes.Api.Models;
using System.Threading.Tasks;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IOrganizationSubscriptionRepository
    {
        Task<OrganizationSubscription?> GetByOrganizationIdAsync(string organizationId);
        Task<OrganizationSubscription?> GetByIdAsync(string id);
        Task CreateAsync(OrganizationSubscription subscription);
        Task UpdateAsync(OrganizationSubscription subscription);
    }
}
