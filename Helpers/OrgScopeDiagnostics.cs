namespace RafeeqyNotes.Api.Helpers
{
    /// <summary>
    /// Counts and reports every time organization resolution has to fall back from a flat parent
    /// id to walking the embedded snapshot.
    /// </summary>
    /// <remarks>
    /// This exists to answer one question with evidence rather than argument: <em>is it safe to
    /// delete the embedded parent snapshots?</em>
    ///
    /// Those snapshots are why creating one task ships a four-level nested object graph, and why
    /// a 25-member organization has its member list copied into every task document. They cannot
    /// be removed on the strength of a migration reporting success, because the failure mode is
    /// silent: a query that resolves nothing returns an empty list, not an error. So resolution
    /// prefers the flat id, keeps the walk as a fallback, and shouts every time it needs it.
    ///
    /// A run of real traffic with this counter at zero is the evidence. A non-zero count names
    /// the exact documents the backfill could not reach.
    ///
    /// There is no test project here, so this is not a substitute for one - it is the closest
    /// thing to a regression signal this codebase can currently produce.
    /// </remarks>
    public static class OrgScopeDiagnostics
    {
        private static long _fallbacks;
        private static ILogger _logger;

        /// <summary>Wired once at startup so fallbacks land in the normal log stream.</summary>
        public static void Use(ILogger logger) => _logger = logger;

        /// <summary>Total fallbacks since process start.</summary>
        public static long FallbackCount => Interlocked.Read(ref _fallbacks);

        /// <summary>
        /// Records that <paramref name="kind"/> could not be answered from a flat id and had to
        /// walk the embedded graph for <paramref name="entityId"/>.
        /// </summary>
        public static void Fallback(string kind, string entityId)
        {
            var total = Interlocked.Increment(ref _fallbacks);
            var message = "OrgScope fell back to the embedded snapshot: {Kind} for entity {EntityId}. "
                        + "Flat parent ids are missing on this document; the backfill migration did "
                        + "not reach it. Fallbacks since start: {Total}";

            if (_logger != null) _logger.LogWarning(message, kind, entityId ?? "(null)", total);
            else Console.WriteLine($"[orgscope] fallback: {kind} for entity {entityId ?? "(null)"} (total {total})");
        }
    }
}
