using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>What a pending SSO login remembers between the redirect and the callback.</summary>
    public sealed record SsoState(
        string Provider,
        string OrganizationId,
        string Nonce,
        string ReturnUrl,
        DateTime ExpiresAtUtc);

    public interface ISsoStateService
    {
        /// <summary>Issues a single-use state and the nonce to send with it.</summary>
        (string State, string Nonce) Issue(string provider, string organizationId, string returnUrl);

        /// <summary>Redeems a state exactly once.</summary>
        bool TryConsume(string state, out SsoState value);
    }

    /// <summary>
    /// The <c>state</c> and <c>nonce</c> for an SSO login.
    /// </summary>
    /// <remarks>
    /// Both existing social flows sent NEITHER, which is two separate holes:
    ///
    /// - Without <c>state</c>, an attacker can start a login, capture the resulting authorization
    ///   code URL, and trick a victim into completing it - logging the victim into the ATTACKER'S
    ///   account, where anything they then write is visible to the attacker. That is login CSRF,
    ///   and state is the only defence a browser redirect can carry.
    /// - Without <c>nonce</c>, an id_token obtained anywhere else for the same client can be
    ///   replayed into our callback. The nonce ties one token to one login attempt.
    ///
    /// Shaped after IGitHubOAuthStateService, which already got this right for the GitHub connect
    /// flow, but carries a payload: which provider, which organization's SSO, and where to return.
    ///
    /// In-memory, so a restart invalidates pending logins - the user retries and it works. Across
    /// several instances a login must return to the instance that began it, which is the same
    /// constraint the GitHub flow already lives with; a shared store is the fix if that changes.
    /// </remarks>
    public sealed class SsoStateService : ISsoStateService
    {
        /// <summary>Long enough to sign in with MFA, short enough that a leaked state is stale.</summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

        private readonly ConcurrentDictionary<string, SsoState> _pending = new(StringComparer.Ordinal);

        public (string State, string Nonce) Issue(string provider, string organizationId, string returnUrl)
        {
            Prune();

            var state = Random256();
            var nonce = Random256();

            _pending[state] = new SsoState(
                provider, organizationId, nonce, returnUrl, DateTime.UtcNow.Add(Lifetime));

            return (state, nonce);
        }

        public bool TryConsume(string state, out SsoState value)
        {
            value = null;
            if (string.IsNullOrEmpty(state)) return false;

            // Removing is what makes it single-use, and it is atomic.
            if (!_pending.TryRemove(state, out var entry)) return false;
            if (entry.ExpiresAtUtc < DateTime.UtcNow) return false;

            value = entry;
            return true;
        }

        /// <summary>256 bits of CSPRNG entropy, URL-safe.</summary>
        private static string Random256() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _pending)
            {
                if (kvp.Value.ExpiresAtUtc < now) _pending.TryRemove(kvp.Key, out _);
            }
        }
    }

    public interface ISsoTicketService
    {
        /// <summary>Stores a freshly minted JWT behind a single-use code.</summary>
        string Issue(string jwt);

        /// <summary>Redeems the code exactly once, returning the JWT.</summary>
        bool TryConsume(string code, out string jwt);
    }

    /// <summary>
    /// A one-time code the browser swaps for the session token.
    /// </summary>
    /// <remarks>
    /// Both social callbacks used to finish with <c>Redirect($"{frontend}/auth?token={jwt}")</c>,
    /// putting a 30-day session token in a URL. That leaks it into browser history, the Referer
    /// header of every asset the landing page loads, any analytics or error reporter on that page,
    /// server and proxy access logs, and the user's synced-across-devices history. It is also
    /// trivially shoulder-surfed and shareable by accident.
    ///
    /// The callback now redirects with a short-lived opaque code that the app POSTs back once. The
    /// token itself only ever travels in a response body over TLS.
    /// </remarks>
    public sealed class SsoTicketService : ISsoTicketService
    {
        /// <summary>Seconds, not minutes: the app redeems it on the next page load.</summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);

        private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

        private sealed record Entry(string Jwt, DateTime ExpiresAtUtc);

        public string Issue(string jwt)
        {
            Prune();

            var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            _tickets[code] = new Entry(jwt, DateTime.UtcNow.Add(Lifetime));
            return code;
        }

        public bool TryConsume(string code, out string jwt)
        {
            jwt = null;
            if (string.IsNullOrEmpty(code)) return false;

            if (!_tickets.TryRemove(code, out var entry)) return false;
            if (entry.ExpiresAtUtc < DateTime.UtcNow) return false;

            jwt = entry.Jwt;
            return true;
        }

        private void Prune()
        {
            var now = DateTime.UtcNow;
            foreach (var kvp in _tickets)
            {
                if (kvp.Value.ExpiresAtUtc < now) _tickets.TryRemove(kvp.Key, out _);
            }
        }
    }
}
