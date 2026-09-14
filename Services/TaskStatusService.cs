using Microsoft.Extensions.Caching.Memory;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Services
{
    public interface ITaskStatusService
    {
        /// <summary>The organization's workflow, seeded with the defaults on first use.</summary>
        Task<TaskStatusSet> ForOrganizationAsync(string organizationId);

        /// <summary>Custom field slugs declared for an entity type ("task", "note", ...).</summary>
        Task<IReadOnlyCollection<string>> CustomFieldSlugsAsync(string organizationId, string appliesTo);

        /// <summary>Drops the cached workflow after an edit.</summary>
        void Invalidate(string organizationId);
    }

    /// <summary>
    /// An organization's resolved task statuses, and the questions the rest of the code asks
    /// about them.
    /// </summary>
    /// <remarks>
    /// Lookup is by folded key - lowercased with spaces, hyphens and underscores removed - and
    /// then by alias. That single rule absorbs the three-way disagreement already in the data
    /// ("To-do" vs "todo", "Done" vs "done", "In Progress" vs "in_progress") without any of it
    /// having to be rewritten first. It never collapses two real slugs: all five stay distinct
    /// under folding.
    /// </remarks>
    public class TaskStatusSet
    {
        private readonly Dictionary<string, TaskStatusDefinition> _byKey;

        public TaskStatusSet(IEnumerable<TaskStatusDefinition> statuses)
        {
            Statuses = (statuses ?? Enumerable.Empty<TaskStatusDefinition>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Slug))
                .OrderBy(s => s.Order)
                .ToList();

            _byKey = new Dictionary<string, TaskStatusDefinition>(StringComparer.Ordinal);
            foreach (var status in Statuses)
            {
                Register(status.Slug, status);
                foreach (var alias in status.Aliases ?? new List<string>()) Register(alias, status);
            }
        }

        public IReadOnlyList<TaskStatusDefinition> Statuses { get; }

        /// <summary>Slugs meaning "finished". Feeds every completion count and the velocity sum.</summary>
        public IReadOnlyCollection<string> TerminalSlugs =>
            Statuses.Where(s => s.IsTerminal).Select(s => s.Slug).ToList();

        /// <summary>Slug for a task created without a status.</summary>
        public string DefaultSlug =>
            Statuses.FirstOrDefault(s => s.IsDefault)?.Slug
            ?? Statuses.FirstOrDefault()?.Slug
            ?? StatusCatalogue.DefaultSlug;

        private void Register(string key, TaskStatusDefinition status)
        {
            var folded = Fold(key);
            // First registration wins, so a slug always beats another status's alias.
            if (!string.IsNullOrEmpty(folded) && !_byKey.ContainsKey(folded)) _byKey[folded] = status;
        }

        private static string Fold(string raw) =>
            string.IsNullOrWhiteSpace(raw)
                ? null
                : new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>The definition a raw stored value means, or null if nothing recognises it.</summary>
        public TaskStatusDefinition Find(string raw)
        {
            var folded = Fold(raw);
            if (folded == null) return null;
            return _byKey.TryGetValue(folded, out var found) ? found : null;
        }

        /// <summary>Canonical slug for a raw value; the raw value itself when unrecognised.</summary>
        public string Resolve(string raw) => Find(raw)?.Slug ?? raw;

        /// <summary>
        /// Whether a raw stored value means the work is finished.
        /// </summary>
        /// <remarks>
        /// Unrecognised statuses are NOT terminal. That direction is deliberate: under-counting
        /// completion shows a task as still open, while over-counting silently removes it from
        /// everyone's board.
        /// </remarks>
        public bool IsTerminal(string raw) => Find(raw)?.IsTerminal ?? false;

        /// <summary>Whether a raw stored value means work has begun.</summary>
        public bool IsStarted(string raw) => Find(raw)?.IsStarted ?? false;
    }

    /// <summary>
    /// Resolves an organization's task-status workflow.
    /// </summary>
    /// <remarks>
    /// Shaped after EntitlementService, and for the same reason: this is consulted on paths that
    /// run per request, and resolving it is a Mongo read.
    ///
    /// What it replaces is a vocabulary defined in about 26 places across the frontend and 10 in
    /// the backend, no two of which fully agreed. The backend sites now all ask this service.
    /// </remarks>
    public class TaskStatusService : ITaskStatusService
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        private readonly IOrgWorkspaceConfigRepository _configs;
        private readonly IMemoryCache _cache;
        private readonly ILogger<TaskStatusService> _logger;

        public TaskStatusService(
            IOrgWorkspaceConfigRepository configs,
            IMemoryCache cache,
            ILogger<TaskStatusService> logger)
        {
            _configs = configs;
            _cache = cache;
            _logger = logger;
        }

        private static string CacheKey(string orgId) => $"taskstatuses:{orgId}";

        public void Invalidate(string organizationId)
        {
            if (!string.IsNullOrWhiteSpace(organizationId)) _cache.Remove(CacheKey(organizationId));
        }

        public async Task<IReadOnlyCollection<string>> CustomFieldSlugsAsync(string organizationId, string appliesTo)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return Array.Empty<string>();

            try
            {
                var config = await _configs.GetByOrganizationIdAsync(organizationId);
                return (config?.CustomFields ?? new List<Models.CustomFieldDefinition>())
                    .Where(f => !f.IsArchived
                                && string.Equals(f.AppliesTo, appliesTo, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Slug)
                    .ToList();
            }
            catch (Exception ex)
            {
                // Fail CLOSED here, unlike the status set. An empty declared list rejects any
                // custom field on the write rather than storing values nothing can render.
                _logger.LogWarning(ex,
                    "Custom field registry unavailable for {OrganizationId}.", organizationId);
                return Array.Empty<string>();
            }
        }

        public async Task<TaskStatusSet> ForOrganizationAsync(string organizationId)
        {
            // No organization still answers with the defaults rather than an empty set. An empty
            // set would make every status unrecognised and therefore non-terminal, quietly
            // zeroing every completion count.
            if (string.IsNullOrWhiteSpace(organizationId))
            {
                return new TaskStatusSet(StatusCatalogue.Defaults());
            }

            if (_cache.TryGetValue(CacheKey(organizationId), out TaskStatusSet cached)) return cached;

            TaskStatusSet resolved;
            try
            {
                var config = await _configs.EnsureAsync(organizationId, StatusCatalogue.Defaults());
                resolved = new TaskStatusSet(
                    config?.Statuses is { Count: > 0 } ? config.Statuses : StatusCatalogue.Defaults());
            }
            catch (Exception ex)
            {
                // Same failure direction as above: fall back to the defaults, never to nothing.
                _logger.LogWarning(ex,
                    "Task status registry unavailable for {OrganizationId}; using defaults.", organizationId);
                resolved = new TaskStatusSet(StatusCatalogue.Defaults());
            }

            _cache.Set(CacheKey(organizationId), resolved, CacheFor);
            return resolved;
        }
    }
}
