using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="IOrgWorkspaceConfigRepository"/>
    public class OrgWorkspaceConfigRepository : IOrgWorkspaceConfigRepository
    {
        private readonly IMongoCollection<OrgWorkspaceConfig> _configs;

        // No index creation here. Every other repository in this codebase builds its indexes in
        // the constructor, which runs inside the DI block in Program.cs - a block whose catch
        // turns any failure into a blanket 500 on every route. The unique index on
        // OrganizationId is built by SchemaIndexes instead, after the app is built.
        public OrgWorkspaceConfigRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _configs = database.GetCollection<OrgWorkspaceConfig>("OrgWorkspaceConfig");
        }

        public async Task<OrgWorkspaceConfig> GetByOrganizationIdAsync(string organizationId)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return null;

            return await _configs
                .Find(c => c.OrganizationId == organizationId)
                .FirstOrDefaultAsync();
        }

        /// <remarks>
        /// Seeds lazily, on first read for an organization, rather than looping every existing
        /// organization at startup. New organizations then get the defaults automatically with no
        /// extra step in the signup path.
        ///
        /// The insert races another request seeding the same organization; the unique index on
        /// OrganizationId decides, and the loser re-reads the winner's document rather than
        /// failing the request.
        /// </remarks>
        public async Task<OrgWorkspaceConfig> EnsureAsync(string organizationId, List<TaskStatusDefinition> seed)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return null;

            var existing = await GetByOrganizationIdAsync(organizationId);
            if (existing != null) return existing;

            var created = new OrgWorkspaceConfig
            {
                OrganizationId = organizationId,
                Statuses = seed,
            };

            try
            {
                await _configs.InsertOneAsync(created);
                return created;
            }
            catch (MongoWriteException ex)
                when (ex.WriteError != null && ex.WriteError.Code == 11000)
            {
                return await GetByOrganizationIdAsync(organizationId);
            }
        }

        public async Task UpsertAsync(OrgWorkspaceConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.OrganizationId)) return;

            config.UpdatedAt = DateTime.UtcNow;

            await _configs.ReplaceOneAsync(
                c => c.OrganizationId == config.OrganizationId,
                config,
                new ReplaceOptions { IsUpsert = true });
        }
    }
}
