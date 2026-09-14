namespace RafeeqyNotes.Api.Config
{
    public class ElasticSearchSettings
    {
        public string Uri { get; set; } = string.Empty;
        public string NotesIndex { get; set; } = string.Empty;

        /// <summary>
        /// Whether to talk to Elasticsearch at all. Off by default.
        /// </summary>
        /// <remarks>
        /// Off because nothing reads from it. <c>SearchController</c> queries MongoDB directly,
        /// and <c>SearchNotesAsync</c> has no callers anywhere in the codebase - the cluster was
        /// being written to and never read.
        ///
        /// Leaving it on by default was expensive in a way nothing reported. With no cluster
        /// listening, the client spends ~4.2s per call failing to reach it: two of those in the
        /// old constructor (8.3s on the first request after every app-pool recycle) and one more
        /// on every note save, signup and profile edit. None of it surfaced, because the client
        /// returns a failed *response object* rather than throwing, so the catch never fired.
        ///
        /// Turn it on only alongside a reachable cluster AND a reader that uses it.
        /// </remarks>
        public bool Enabled { get; set; }

        /// <summary>
        /// How long a single Elasticsearch call may take before it is abandoned.
        /// </summary>
        /// <remarks>
        /// Short on purpose. Indexing is a secondary concern behind the write that triggered it,
        /// so a slow cluster must cost the user a moment at worst, never a request.
        /// </remarks>
        public int RequestTimeoutSeconds { get; set; } = 5;
    }
}
