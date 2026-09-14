using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Services;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Models;
using Microsoft.AspNetCore.Mvc;
using Nest;
using Microsoft.Extensions.Hosting;

namespace RafeeqyNotes.Api.Repositories
{
    public class NoteTaskRepository : INoteTaskRepository
    {
        private readonly IMongoCollection<NoteTask> _tasks;
        private readonly IMongoCollection<Contributor> _contributor;

        private readonly IEntityGraphService _graph;

        public NoteTaskRepository(MongoDbSettings settings, IEntityGraphService graph)
        {
            _graph = graph;
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _tasks = database.GetCollection<NoteTask>("NoteTasks");
            _contributor = database.GetCollection<Contributor>(settings.ContributorsCollection);
        }

        // Queries the flat id rather than the embedded path. Equivalent by construction:
        // M001 copies the embedded value verbatim, so a document missing the flat id is a
        // document whose embedded path was missing too - and it now uses an index instead of
        // scanning the collection.
        public async Task<List<NoteTask>> GetByNoteIdAsync(string noteId) =>
            await HydratedAsync(await _tasks.Find(t => t.NoteId == noteId).ToListAsync());

        public async Task<List<NoteTask>> GetByProjectIdAsync(string projectId) =>
            await HydratedAsync(await _tasks.Find(t => t.ProjectId == projectId).ToListAsync());

        public async Task<NoteTask?> GetByIdAsync(string id)
        {
            var task = await _tasks.Find(t => t.Id == id).FirstOrDefaultAsync();
            if (task != null) await HydratedAsync(new List<NoteTask> { task });
            return task;
        }
        public async Task<List<NoteTask>> GetByCreatorId(string contributorId)=>
            await HydratedAsync(await _tasks.Find(t => t.Creator.Id == contributorId).ToListAsync());
        
        public async Task<List<NoteTask>> GetByReviewerId(string contributorId)=>
            await HydratedAsync(await _tasks.Find(t => t.Reviewer.Id == contributorId).ToListAsync());

        public async Task<List<NoteTask>> GetByAssignedToID(string contributorId)
        {
            var filter = Builders<NoteTask>.Filter.ElemMatch(x => x.AssignedTo, x => x.Id == contributorId);
            return await HydratedAsync(await _tasks.Find(filter).ToListAsync());
        }        
        public async Task<List<NoteTask>> GetBySprintID(string sprintId)
        {
            return await HydratedAsync(await _tasks.Find(t => t.SprintId==sprintId).ToListAsync());
        }

        public async Task<List<NoteTask>> GetWithRunningTimerAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return new List<NoteTask>();

            // One elemMatch, so it is the SAME entry that is both this user's and still open -
            // two separate conditions would also match a task where someone else's timer is
            // running and this user merely has a closed segment.
            var filter = Builders<NoteTask>.Filter.ElemMatch(
                t => t.TimeEntries,
                Builders<TaskTimeEntry>.Filter.And(
                    Builders<TaskTimeEntry>.Filter.Eq(e => e.UserId, userId),
                    Builders<TaskTimeEntry>.Filter.Eq(e => e.EndedAt, null)));

            return await _tasks.Find(filter).ToListAsync();
        }

        /// <summary>
        /// Fills in <see cref="NoteTask.Dependencies"/> from the stored ids, in ONE query.
        /// </summary>
        /// <remarks>
        /// Resolved on every read rather than snapshotted on write, which is half the point of
        /// the change: a dependency's title and status are now whatever they are RIGHT NOW. The
        /// old embedded copies were frozen at the moment the link was made, so a task could keep
        /// showing "Blocked" long after its blocker had finished.
        ///
        /// One round trip for the whole page however many tasks or edges it holds - every id
        /// across every task is gathered first and looked up together. The projection asks for
        /// four fields, so resolving a dependency costs a fraction of what embedding one did.
        ///
        /// Summaries are built by hand rather than handing back the fetched documents, so a
        /// summary can never carry its own Dependencies and the shape cannot turn recursive
        /// again. Tasks with no ids get an empty list, never null - clients call .length on it.
        /// </remarks>
        private async Task<List<NoteTask>> HydratedAsync(List<NoteTask> tasks)
        {
            if (tasks == null || tasks.Count == 0) return tasks ?? new List<NoteTask>();

            var ids = tasks
                .Where(t => t?.DependencyIds != null)
                .SelectMany(t => t.DependencyIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                foreach (var t in tasks) t.Dependencies = new List<TaskDependencySummary>();
                return tasks;
            }

            var projection = Builders<NoteTask>.Projection
                .Include(t => t.Id)
                .Include(t => t.Title)
                .Include(t => t.Status)
                .Include(t => t.Priority);

            var found = await _tasks
                .Find(Builders<NoteTask>.Filter.In(t => t.Id, ids))
                .Project<NoteTask>(projection)
                .ToListAsync();

            var byId = found
                .Where(t => !string.IsNullOrEmpty(t.Id))
                .ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var task in tasks)
            {
                task.Dependencies = (task.DependencyIds ?? new List<string>())
                    // A deleted dependency simply drops out. A placeholder would put a node with
                    // no title on the dependency graph.
                    .Where(id => !string.IsNullOrWhiteSpace(id) && byId.ContainsKey(id))
                    .Select(id => new TaskDependencySummary
                    {
                        Id = byId[id].Id,
                        Title = byId[id].Title,
                        Status = byId[id].Status,
                        Priority = byId[id].Priority,
                    })
                    .ToList();
            }

            return tasks;
        }

        /// <summary>
        /// Reduces whatever the client sent in <c>Dependencies</c> to a list of ids.
        /// </summary>
        /// <remarks>
        /// Clients still post whole task objects here - the shape predates this change and did
        /// not have to be updated for it to be safe. Only the ids survive, so no write path can
        /// restore the old embedded form.
        ///
        /// Null means "not supplied": the caller is editing something else and the stored links
        /// must be left alone. An empty list means "no dependencies", which is a real edit. The
        /// controller substitutes the stored value when the body says nothing.
        /// </remarks>
        private static void NormalizeDependencies(NoteTask task)
        {
            if (task == null) return;

            if (task.Dependencies != null)
            {
                task.DependencyIds = task.Dependencies
                    .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Id))
                    .Select(d => d.Id)
                    // Self-dependency is a cycle of length one: the graph would draw an edge from
                    // a node to itself and read it as permanently blocking.
                    .Where(id => !string.Equals(id, task.Id, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            task.DependencyIds ??= new List<string>();
        }

        public async Task CreateAsync(NoteTask task)
        {
            // NoteTask never had an id assigned anywhere - not on the model, not in the
            // repository - so a request without one wrote _id: null, and the second such
            // write collided on the _id index with an unhandled 500.
            if (string.IsNullOrWhiteSpace(task.Id)) task.Id = Guid.NewGuid().ToString();

            NormalizeDependencies(task);

            await _graph.HydrateTaskAsync(task);

            try { await _tasks.InsertOneAsync(task); }
            catch (MongoWriteException ex) when (DuplicateEntityException.IsDuplicateKey(ex))
            { throw new DuplicateEntityException("task", task.Id, ex); }
        }


        // Tenancy is re-derived from the STORED record, never from the request body. These
        // endpoints replace the whole document, so without this a caller who may edit a
        // resource can reparent it by naming a different parent in the body - and every guard
        // downstream then authorizes against the organization the caller chose. This is the
        // same "authorize against the stored record" rule the Phase 0 guards follow.
        public async Task UpdateAsync(NoteTask task)
        {
            var stored = await _tasks.Find(t => t.Id == task.Id).FirstOrDefaultAsync();
            await _graph.HydrateTaskAsync(task, stored?.NoteId ?? stored?.Note?.Id);

            NormalizeDependencies(task);

            await _tasks.ReplaceOneAsync(t => t.Id == task.Id, task);
        }

        public async Task DeleteAsync(string id) =>
            await _tasks.DeleteOneAsync(t => t.Id == id);
    }
}