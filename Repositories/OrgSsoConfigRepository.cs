using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="IOrgSsoConfigRepository"/>
    public class OrgSsoConfigRepository : IOrgSsoConfigRepository
    {
        private readonly IMongoCollection<OrgSsoConfig> _configs;

        public OrgSsoConfigRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _configs = database.GetCollection<OrgSsoConfig>("OrgSsoConfigs");
        }

        public async Task<OrgSsoConfig> GetByOrganizationIdAsync(string organizationId)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return null;
            return await _configs.Find(c => c.OrganizationId == organizationId).FirstOrDefaultAsync();
        }

        /// <remarks>
        /// Only ENABLED configs are matched. A half-finished config must not start routing real
        /// logins, and a disabled one must stop immediately.
        /// </remarks>
        public async Task<OrgSsoConfig> FindByEmailDomainAsync(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return null;

            var normalized = domain.Trim().ToLowerInvariant();

            var filter = Builders<OrgSsoConfig>.Filter.And(
                Builders<OrgSsoConfig>.Filter.Eq(c => c.Enabled, true),
                Builders<OrgSsoConfig>.Filter.Or(
                    Builders<OrgSsoConfig>.Filter.AnyEq(c => c.AllowedEmailDomains, normalized),
                    Builders<OrgSsoConfig>.Filter.Eq(c => c.GoogleHostedDomain, normalized)));

            return await _configs.Find(filter).FirstOrDefaultAsync();
        }

        public async Task UpsertAsync(OrgSsoConfig config)
        {
            config.UpdatedAt = DateTime.UtcNow;

            await _configs.ReplaceOneAsync(
                c => c.OrganizationId == config.OrganizationId,
                config,
                new ReplaceOptions { IsUpsert = true });
        }

        public async Task DeleteAsync(string organizationId) =>
            await _configs.DeleteOneAsync(c => c.OrganizationId == organizationId);
    }
}
