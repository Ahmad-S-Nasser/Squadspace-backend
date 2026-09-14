using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>
    /// Issues and redeems the OAuth <c>state</c> value for the GitHub connect flow.
    /// </summary>
    /// <remarks>
    /// The GitHub callback is a browser redirect and cannot carry a bearer token, so
    /// <c>state</c> is the only thing tying the returning request back to the user who
    /// started it. It must therefore be unguessable, single-use and short-lived.
    ///
    /// Previously <c>state</c> was simply the caller-supplied <c>userId</c>, which meant an
    /// attacker could begin the flow naming a victim, complete it with their own GitHub
    /// account, and overwrite the victim's stored token.
    /// </remarks>
    public interface IGitHubOAuthStateService
    {
        /// <summary>Issues a single-use state token bound to <paramref name="userId"/>.</summary>
        string Issue(string userId);

        /// <summary>
        /// Redeems a state token exactly once. Returns false if it is unknown, already used,
        /// or expired.
        /// </summary>
        bool TryConsume(string state, out string userId);
    }

    public sealed class GitHubOAuthStateService : IGitHubOAuthStateService
    {
        // Long enough for the user to complete GitHub's consent screen, short enough that a
        // leaked state is not durable.
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);

        private sealed record Entry(string UserId, DateTime ExpiresAtUtc);

        public string Issue(string userId)
        {
            if (string.IsNullOrEmpty(userId)) throw new ArgumentException("userId is required", nameof(userId));

            Prune();

            // 256 bits of CSPRNG entropy, URL-safe.
            var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            _pending[state] = new Entry(userId, DateTime.UtcNow.Add(Lifetime));
            return state;
        }

        public bool TryConsume(string state, out string userId)
        {
            userId = null;
            if (string.IsNullOrEmpty(state)) return false;

            // Removing is what makes it single-use, and it is atomic.
            if (!_pending.TryRemove(state, out var entry)) return false;
            if (entry.ExpiresAtUtc < DateTime.UtcNow) return false;

            userId = entry.UserId;
            return true;
        }

        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _pending)
            {
                if (kvp.Value.ExpiresAtUtc < now) _pending.TryRemove(kvp.Key, out _);
            }
        }
    }
}
