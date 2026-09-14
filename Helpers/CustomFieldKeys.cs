using System.Text.RegularExpressions;

namespace RafeeqyNotes.Api.Helpers
{
    /// <summary>
    /// Validates custom-field keys before they reach a document.
    /// </summary>
    /// <remarks>
    /// Not a style rule. A BSON key containing a "." is interpreted as a path, and one starting
    /// with "$" as an operator - either produces a document that cannot be queried or updated
    /// through ordinary driver calls, and the damage is only visible later, on read. Rejecting at
    /// the edge with a 400 is the difference between a bad request and an unreachable record.
    ///
    /// The same shape as a status slug, deliberately: one identity rule for the whole registry.
    /// </remarks>
    public static class CustomFieldKeys
    {
        public const string Pattern = "^[a-z][a-z0-9_]{0,39}$";

        private static readonly Regex Valid = new(Pattern, RegexOptions.Compiled);

        public static bool IsValid(string slug) =>
            !string.IsNullOrWhiteSpace(slug) && Valid.IsMatch(slug);

        /// <summary>
        /// Keys in <paramref name="bag"/> that are malformed or not declared for this entity type.
        /// </summary>
        /// <remarks>
        /// Unknown keys are refused rather than silently stored. A field nobody declared cannot be
        /// rendered, filtered or reported on, so accepting it would recreate exactly the failure
        /// this phase started from: the client sending <c>category</c> for months into a backend
        /// that quietly dropped it.
        /// </remarks>
        public static List<string> Rejected(IDictionary<string, string> bag, IEnumerable<string> declared)
        {
            if (bag == null || bag.Count == 0) return new List<string>();

            var known = new HashSet<string>(declared ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            return bag.Keys
                .Where(k => !IsValid(k) || !known.Contains(k))
                .ToList();
        }
    }
}
