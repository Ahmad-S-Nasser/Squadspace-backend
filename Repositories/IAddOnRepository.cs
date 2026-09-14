using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IAddOnRepository
    {
        /// <summary>The purchasable catalogue.</summary>
        Task<List<AddOn>> GetCatalogueAsync();

        Task UpsertCatalogueEntryAsync(AddOn addOn);

        /// <summary>Active add-ons an organization has bought.</summary>
        Task<List<OrganizationAddOn>> GetActiveForOrganizationAsync(string organizationId);

        Task UpsertPurchaseAsync(OrganizationAddOn purchase);
        Task RemovePurchaseAsync(string id);
    }
}
