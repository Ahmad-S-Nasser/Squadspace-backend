using Nest;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>
    /// Mirrors notes and contributors into Elasticsearch, when there is an Elasticsearch to
    /// mirror them into.
    /// </summary>
    /// <remarks>
    /// **Disabled by default.** See <see cref="ElasticSearchSettings.Enabled"/> for why: nothing
    /// in the product reads from this index. Search is served from MongoDB.
    ///
    /// Two rules govern everything below, and both exist because this class used to break them:
    ///
    /// 1. **The constructor performs no I/O.** It used to call <c>Indices.Exists</c> and
    ///    <c>Indices.Create</c> synchronously. With no cluster listening those cost 4.2s each,
    ///    and because this is a lazily-resolved singleton the bill landed on whichever user
    ///    happened to open a note first after an app-pool recycle - 8.3 seconds, measured.
    ///
    /// 2. **Indexing never blocks the write that triggered it.** A search index is derived data.
    ///    Someone saving a note should not wait on it, and must never see their save fail
    ///    because of it. Calls are dispatched off the request path and their outcome is logged.
    ///
    /// Failures are logged rather than thrown, but they ARE logged - the NEST client reports
    /// failure in the returned response instead of raising, so <c>IsValid</c> has to be checked
    /// explicitly. Not checking it is how the original silently stopped working.
    /// </remarks>
    public class ElasticSearchService
    {
        private readonly IElasticClient _client;
        private readonly string _index;
        private readonly ILogger<ElasticSearchService> _logger;

        /// <summary>Guards the one-time index creation without blocking anything.</summary>
        private readonly SemaphoreSlim _indexGate = new(1, 1);
        private bool _indexReady;

        public ElasticSearchService(ElasticSearchSettings settings, ILogger<ElasticSearchService> logger)
        {
            _logger = logger;
            _index = settings.NotesIndex;

            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.Uri))
            {
                // Null client, and every method below short-circuits on it. Nothing is
                // constructed, no socket is opened, and the service costs nothing at all.
                _client = null;
                _logger.LogInformation(
                    "Elasticsearch is disabled; note and contributor indexing is a no-op. " +
                    "Search is served from MongoDB.");
                return;
            }

            var connection = new ConnectionSettings(new Uri(settings.Uri))
                .DefaultIndex(settings.NotesIndex)
                .DefaultMappingFor<Note>(m => m.IdProperty(n => n.Id))
                // Bounded so a cluster that is merely unreachable cannot hold a background
                // task open for the client's much longer default.
                .RequestTimeout(TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds)))
                .MaxRetryTimeout(TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds)));

            _client = new ElasticClient(connection);

            _logger.LogInformation("Elasticsearch enabled at {Uri}, index {Index}.", settings.Uri, _index);
        }

        public bool IsEnabled => _client != null;

        public Task IndexBoardAsync(Board board) => DispatchAsync("index board", c => c.IndexDocumentAsync(board));

        public Task IndexNoteAsync(Note note) => DispatchAsync("index note", c => c.IndexDocumentAsync(note));

        public Task IndexContributorAsync(Contributor contributor) =>
            DispatchAsync("index contributor", c => c.IndexDocumentAsync(contributor));

        public Task DeleteContributorAsync(string id) =>
            DispatchAsync("delete contributor", c => c.DeleteAsync<Contributor>(id, d => d.Index(_index)));

        /// <summary>
        /// Full-text note search.
        /// </summary>
        /// <remarks>
        /// Currently has no callers - SearchController queries MongoDB. Kept because it is the
        /// starting point if search ever moves here, and returns an empty list rather than
        /// throwing when disabled so a future caller degrades instead of erroring.
        /// </remarks>
        public async Task<List<Note>> SearchNotesAsync(string query)
        {
            if (_client == null) return new List<Note>();

            var response = await _client.SearchAsync<Note>(s => s
                .Query(q => q
                    .MultiMatch(mm => mm
                        .Query(query)
                        .Fields(f => f
                            .Field(n => n.Title)
                            .Field(n => n.Content)
                            .Field(n => n.Tags)
                        )
                    )
                )
            );

            if (!response.IsValid)
            {
                _logger.LogWarning("Elasticsearch search failed: {Reason}", Describe(response));
                return new List<Note>();
            }

            return response.Documents.ToList();
        }

        /// <summary>
        /// Runs one indexing call away from the caller's request.
        /// </summary>
        /// <remarks>
        /// Returns a completed task immediately, so <c>await</c> at the call sites costs nothing
        /// and the repositories did not have to change. The work itself continues on the thread
        /// pool; it is derived data, so losing an in-flight index on shutdown costs a re-index,
        /// not a user's write.
        ///
        /// Nothing escapes: the delegate is fully wrapped, because an unobserved exception on a
        /// background task is the one kind that can still take a process down.
        /// </remarks>
        private Task DispatchAsync<TResponse>(string what, Func<IElasticClient, Task<TResponse>> call)
            where TResponse : IResponse
        {
            if (_client == null) return Task.CompletedTask;

            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureIndexAsync();

                    var response = await call(_client);
                    if (!response.IsValid)
                    {
                        _logger.LogWarning("Elasticsearch could not {What}: {Reason}", what, Describe(response));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Elasticsearch could not {What}.", what);
                }
            });

            return Task.CompletedTask;
        }

        /// <summary>Creates the index once, on the background path, never in the constructor.</summary>
        private async Task EnsureIndexAsync()
        {
            if (_indexReady || _client == null) return;

            await _indexGate.WaitAsync();
            try
            {
                if (_indexReady) return;

                var exists = await _client.Indices.ExistsAsync(_index);

                // A failed check is not "the index is missing" - trying to create it would then
                // fail too and log twice for one problem. Leave _indexReady false so the next
                // write retries, and let the write's own failure do the reporting.
                if (!exists.IsValid) return;

                if (!exists.Exists)
                {
                    var created = await _client.Indices.CreateAsync(_index, c => c.Map<Note>(m => m.AutoMap()));
                    if (!created.IsValid)
                    {
                        _logger.LogWarning("Could not create Elasticsearch index {Index}: {Reason}",
                            _index, Describe(created));
                        return;
                    }
                }

                _indexReady = true;
            }
            finally
            {
                _indexGate.Release();
            }
        }

        private static string Describe(IResponse response) =>
            response.OriginalException?.Message
            ?? response.ServerError?.ToString()
            ?? "no detail";
    }
}
