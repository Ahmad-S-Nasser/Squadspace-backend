using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Services;
using RafeeqyNotes.Api.Helpers;

namespace RafeeqyNotes.Api.Repositories
{
    public class ProjectRepository : IProjectRepository
    {
        private readonly IMongoCollection<Project> _projects;
        private readonly IEntityGraphService _graph;

        public ProjectRepository(MongoDbSettings settings, IEntityGraphService graph)
        {
            _graph = graph;
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _projects = database.GetCollection<Project>("Projects");
        }

        public async Task<List<Project>> GetAllAsync() =>
            await _projects.Find(_ => true).ToListAsync();

        // Queries the flat id rather than the embedded path. Equivalent by construction:
        // M001 copies the embedded value verbatim, so a document missing the flat id is a
        // document whose embedded path was missing too - and it now uses an index instead of
        // scanning the collection.
        public async Task<List<Project>> GetByOrganizationIdAsync(string organizationId)
        {
            var filter = Builders<Project>.Filter.Eq(p => p.OrganizationId, organizationId);
            return await _projects.Find(filter).ToListAsync();
        }

        public async Task<Project?> GetByIdAsync(string id) =>
            await _projects.Find(p => p.Id == id).FirstOrDefaultAsync();

        public async Task<List<OrganizationMember>> GetMembersByProjectID(string id)
        {
            List<OrganizationMember> members =  _projects.Find(p => p.Id == id).FirstOrDefaultAsync().Result.Members;
            return members;
        }

        public async Task CreateAsync(Project project)
        {
            // Preserve a caller-supplied id instead of overwriting it. An importer has to be
            // able to choose ids, or a re-run creates duplicates rather than colliding. Same
            // shape as ContributorRepository.CreateAsync.
            if (string.IsNullOrWhiteSpace(project.Id)) project.Id = Guid.NewGuid().ToString();

            await _graph.HydrateProjectAsync(project);

            try { await _projects.InsertOneAsync(project); }
            catch (MongoWriteException ex) when (DuplicateEntityException.IsDuplicateKey(ex))
            { throw new DuplicateEntityException("project", project.Id, ex); }
        }


        // Tenancy is re-derived from the STORED record, never from the request body. These
        // endpoints replace the whole document, so without this a caller who may edit a
        // resource can reparent it by naming a different parent in the body - and every guard
        // downstream then authorizes against the organization the caller chose. This is the
        // same "authorize against the stored record" rule the Phase 0 guards follow.
        public async Task UpdateAsync(Project project)
        {
            var stored = await _projects.Find(p => p.Id == project.Id).FirstOrDefaultAsync();
            await _graph.HydrateProjectAsync(
                project, stored?.OrganizationId ?? stored?.Organization?.Id);

            await _projects.ReplaceOneAsync(b => b.Id == project.Id, project);
        }

        public async Task DeleteAsync(string id) =>
            await _projects.DeleteOneAsync(b => b.Id == id);
    }       
}