using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="IAddOnRepository"/>
    public class AddOnRepository : IAddOnRepository
    {
        private readonly IMongoCollection<AddOn> _catalogue;
        private readonly IMongoCollection<OrganizationAddOn> _purchases;

        public AddOnRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _catalogue = database.GetCollection<AddOn>("AddOns");
            _purchases = database.GetCollection<OrganizationAddOn>("OrganizationAddOns");
        }

        public async Task<List<AddOn>> GetCatalogueAsync() =>
            await _catalogue.Find(_ => true).SortBy(a => a.SortOrder).ToListAsync();

        /// <remarks>
        /// Matched on AddOnId alone, the same rule the plan seeder follows: an upsert filtered on
        /// anything else turns into an insert and collides on the unique key.
        /// </remarks>
        public async Task UpsertCatalogueEntryAsync(AddOn addOn) =>
            await _catalogue.ReplaceOneAsync(
                a => a.AddOnId == addOn.AddOnId, addOn, new ReplaceOptions { IsUpsert = true });

        public async Task<List<OrganizationAddOn>> GetActiveForOrganizationAsync(string organizationId)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return new List<OrganizationAddOn>();

            var b = Builders<OrganizationAddOn>.Filter;
            var now = DateTime.UtcNow;

            return await _purchases.Find(b.And(
                    b.Eq(a => a.OrganizationId, organizationId),
                    b.Eq(a => a.Status, "active"),
                    // An expired add-on must stop granting immediately, not at the next read.
                    b.Or(b.Eq(a => a.EndDate, null), b.Gt(a => a.EndDate, now))))
                .ToListAsync();
        }

        public async Task UpsertPurchaseAsync(OrganizationAddOn purchase)
        {
            purchase.UpdatedAt = DateTime.UtcNow;
            await _purchases.ReplaceOneAsync(
                a => a.Id == purchase.Id, purchase, new ReplaceOptions { IsUpsert = true });
        }

        public async Task RemovePurchaseAsync(string id) =>
            await _purchases.DeleteOneAsync(a => a.Id == id);
    }
}
