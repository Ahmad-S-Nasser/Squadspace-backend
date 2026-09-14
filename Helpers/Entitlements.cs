using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Helpers
{
    /// <summary>The capability keys a plan or add-on can grant.</summary>
    /// <remarks>
    /// Keep this list closed and explicit. An unknown key resolves to the conservative default
    /// in <see cref="EntitlementSet"/> rather than being treated as granted, so a typo denies
    /// rather than silently opening a paid feature.
    /// </remarks>
    public static class Entitlement
    {
        // Boolean capabilities
        public const string GitRepositories = "git.repositories";
        public const string AnalyticsAdvanced = "analytics.advanced";
        public const string Whiteboard = "whiteboard";
        public const string Sprints = "sprints";
        public const string Meetings = "meetings";
        public const string DataExport = "export.data";
        public const string CalendarIntegration = "integrations.calendar";
        public const string Sso = "sso";

        // Numeric quotas
        public const string Seats = "seats";
        public const string StorageGb = "storage.gb";
        public const string AutomationRuns = "automation.runs";

        /// <summary>AI assistant access. Sold as an add-on, never bundled.</summary>
        public const string AiAssistant = "ai.assistant";

        /// <summary>Monthly AI token allowance. Topped up by an add-on.</summary>
        public const string AiTokens = "ai.tokens";

        /// <summary>Priority support. An add-on rather than a plan tier.</summary>
        public const string SupportPriority = "support.priority";

        /// <summary>Value meaning "no ceiling" for any numeric quota.</summary>
        public const string Unlimited = "unlimited";
    }

    /// <summary>
    /// An organization's effective entitlements: its plan's grants with any purchased add-ons
    /// merged over the top.
    /// </summary>
    /// <remarks>
    /// Resolution is ADDITIVE, not a plain lookup. Booleans are OR'd and numeric quotas are
    /// summed, because an add-on tops up what the plan already gives rather than replacing it.
    /// Building it this way from the start matters: a plan-only lookup would have to be
    /// reopened at every call site the first time an add-on is sold.
    /// </remarks>
    public sealed class EntitlementSet
    {
        private readonly Dictionary<string, string> _values;

        /// <summary>Capabilities every plan carries, including none at all.</summary>
        /// <remarks>
        /// These are the things that make the product usable rather than the things it sells:
        /// a workspace with no boards, notes or tasks is not a trial, it is a dead end. The
        /// paid capabilities default to denied.
        /// </remarks>
        private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
        {
            [Entitlement.GitRepositories] = "0",
            [Entitlement.AnalyticsAdvanced] = "false",
            [Entitlement.Whiteboard] = "true",
            [Entitlement.Sprints] = "true",
            [Entitlement.Meetings] = "true",
            [Entitlement.DataExport] = "false",
            [Entitlement.CalendarIntegration] = "false",
            [Entitlement.Sso] = "false",
            [Entitlement.Seats] = "1",
            [Entitlement.StorageGb] = "1",
            [Entitlement.AutomationRuns] = "0",
        };

        public EntitlementSet(IEnumerable<IDictionary<string, string>> layers)
        {
            // Merge the supplied layers together FIRST, then fall back to defaults only for
            // keys no layer mentioned.
            //
            // Seeding _values with Defaults and merging on top does not work: Merge sums
            // numeric quotas, so a plan granting 5 seats landed on the default of 1 and
            // resolved to 6 — every organization silently got one extra seat and one extra
            // gigabyte. Defaults are a floor for unspecified keys, not a layer to add.
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var layer in layers ?? Enumerable.Empty<IDictionary<string, string>>())
            {
                if (layer == null) continue;
                foreach (var kv in layer)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    _values[kv.Key] = Merge(_values.TryGetValue(kv.Key, out var existing) ? existing : null,
                                            kv.Value);
                }
            }

            foreach (var kv in Defaults)
            {
                if (!_values.ContainsKey(kv.Key)) _values[kv.Key] = kv.Value;
            }
        }

        /// <summary>Boolean grants OR together; numeric quotas add up; "unlimited" wins outright.</summary>
        private static string Merge(string existing, string incoming)
        {
            if (string.IsNullOrWhiteSpace(incoming)) return existing;
            if (string.IsNullOrWhiteSpace(existing)) return incoming;

            if (Entitlement.Unlimited.Equals(existing, StringComparison.OrdinalIgnoreCase) ||
                Entitlement.Unlimited.Equals(incoming, StringComparison.OrdinalIgnoreCase))
            {
                return Entitlement.Unlimited;
            }

            if (bool.TryParse(existing, out var a) && bool.TryParse(incoming, out var b))
            {
                return (a || b).ToString().ToLowerInvariant();
            }

            if (long.TryParse(existing, out var x) && long.TryParse(incoming, out var y))
            {
                return (x + y).ToString();
            }

            // Not comparable — the later layer wins.
            return incoming;
        }

        /// <summary>True when a boolean capability is granted, or a numeric quota is above zero.</summary>
        public bool Can(string key)
        {
            var v = Raw(key);
            if (Entitlement.Unlimited.Equals(v, StringComparison.OrdinalIgnoreCase)) return true;
            if (bool.TryParse(v, out var b)) return b;
            if (long.TryParse(v, out var n)) return n > 0;
            return false;
        }

        /// <summary>Numeric ceiling for a quota. Returns <see cref="long.MaxValue"/> for unlimited.</summary>
        public long Limit(string key)
        {
            var v = Raw(key);
            if (Entitlement.Unlimited.Equals(v, StringComparison.OrdinalIgnoreCase)) return long.MaxValue;
            return long.TryParse(v, out var n) ? n : 0;
        }

        public string Raw(string key) =>
            key != null && _values.TryGetValue(key, out var v) ? v : null;

        public IReadOnlyDictionary<string, string> All => _values;
    }

    /// <summary>Outcome of an entitlement check, shaped like <see cref="OrgAuth"/>.</summary>
    public sealed class EntitlementCheck
    {
        public bool Allowed { get; init; }
        public string Key { get; init; }
        public ActionResult Error { get; init; }
    }

    public static class EntitlementGuard
    {
        /// <summary>
        /// "log"     - evaluate and log what WOULD be blocked, but allow it through.
        /// "enforce" - actually block.
        /// </summary>
        /// <remarks>
        /// Deliberately the same rollout mechanism as <c>Auth:OrgGuardMode</c>. Feature gates
        /// change what paying customers can do, so they ship observing-only first: deploy in
        /// "log", read the logs to find gates firing on legitimate use, then flip to "enforce".
        /// Anything other than the literal "log" enforces, so a missing setting fails closed.
        /// </remarks>
        private const string GuardModeKey = "Billing:EntitlementGuardMode";
        private const string LogMode = "log";

        /// <summary>Requires a capability, returning the 402 to send when it is missing.</summary>
        public static EntitlementCheck RequireEntitlement(
            this ControllerBase controller, EntitlementSet entitlements, string key)
        {
            if (entitlements != null && entitlements.Can(key))
            {
                return new EntitlementCheck { Allowed = true, Key = key };
            }

            return Deny(controller, key,
                message: "Not included in your plan",
                detail: $"This organization's plan does not include '{key}'.");
        }

        /// <summary>
        /// Requires headroom in a numeric quota — for example, that an organization has not
        /// already used all the repositories its plan allows.
        /// </summary>
        /// <remarks>
        /// This MUST go through the same guard mode as <see cref="RequireEntitlement"/>. An
        /// earlier version checked the quota inline in the controller and returned 402
        /// directly, which meant "log" mode allowed the capability check and was then blocked
        /// by the quota anyway — the rollout switch silently did nothing. Any new limit check
        /// belongs here rather than at the call site, for exactly that reason.
        /// </remarks>
        public static EntitlementCheck RequireQuota(
            this ControllerBase controller, EntitlementSet entitlements, string key, long currentUsage)
        {
            var limit = entitlements?.Limit(key) ?? 0;
            if (currentUsage < limit)
            {
                return new EntitlementCheck { Allowed = true, Key = key };
            }

            var allowance = limit == long.MaxValue ? "unlimited" : limit.ToString();
            return Deny(controller, key,
                message: "Plan limit reached",
                detail: $"This plan allows {allowance} of '{key}'. Upgrade to add more.");
        }

        /// <summary>
        /// Shared denial path, so every entitlement check honours the rollout switch.
        /// </summary>
        private static EntitlementCheck Deny(
            ControllerBase controller, string key, string message, string detail)
        {
            var config = controller.HttpContext?.RequestServices?.GetService<IConfiguration>();
            var enforcing = !string.Equals(config?[GuardModeKey], LogMode, StringComparison.OrdinalIgnoreCase);

            if (!enforcing)
            {
                var logger = controller.HttpContext?.RequestServices
                    ?.GetService<ILoggerFactory>()?.CreateLogger("EntitlementGuard");
                logger?.LogWarning(
                    "Entitlement would-deny (guard in log mode, request allowed): key={Key} reason={Reason} path={Path}",
                    key, message, controller.HttpContext?.Request?.Path.Value);

                return new EntitlementCheck { Allowed = true, Key = key };
            }

            return new EntitlementCheck
            {
                Allowed = false,
                Key = key,
                // 402 rather than 403: this is not "you may not", it is "your plan does not
                // include this" — a different remedy, and the client shows an upgrade path.
                Error = controller.StatusCode(StatusCodes.Status402PaymentRequired, new
                {
                    message,
                    detail,
                    entitlement = key
                })
            };
        }
    }
}
