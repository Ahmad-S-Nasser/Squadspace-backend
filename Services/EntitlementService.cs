using Microsoft.Extensions.Caching.Memory;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Services
{
    public interface IEntitlementService
    {
        /// <summary>Effective entitlements for an organization: plan grants plus add-ons.</summary>
        Task<EntitlementSet> ForOrganizationAsync(string organizationId);

        /// <summary>Drops the cached answer for an organization after a plan or add-on change.</summary>
        void Invalidate(string organizationId);
    }

    /// <summary>
    /// Resolves what an organization is entitled to.
    /// </summary>
    /// <remarks>
    /// Two things this deliberately centralises, because both were previously duplicated and
    /// inconsistent across controllers:
    ///
    /// 1. THE SOURCE OF TRUTH IS THE ORGANIZATION, not the user. Contributor carries its own
    ///    SubscriptionPlan / MaxMembers fields, but they are never refreshed after login and no
    ///    component reads them; the seat limits that are actually enforced come from
    ///    OrganizationSubscription. Mixing the two is how an org ends up on two plans at once.
    ///
    /// 2. WHAT "NO SUBSCRIPTION" MEANS. Three separate copies of the seat check existed, all
    ///    fail-OPEN — a missing or non-active subscription meant no limit at all, and one of
    ///    them defaulted to a limit of 3 while another applied none. Here it resolves to the
    ///    free plan, so an org without a subscription gets free-tier entitlements rather than
    ///    unlimited ones.
    ///
    /// Resolution is two Mongo reads, and a guard can run on every request, so answers are
    /// cached briefly per organization and invalidated on subscription change.
    /// </remarks>
    public class EntitlementService : IEntitlementService
    {
        /// <summary>Plan applied to an organization with no active subscription.</summary>
        public const string FallbackPlanId = "free";

        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        private readonly IOrganizationSubscriptionRepository _subscriptions;
        private readonly ISubscriptionPlanRepository _plans;
        private readonly IAddOnRepository _addOns;
        private readonly IMemoryCache _cache;
        private readonly ILogger<EntitlementService> _logger;

        public EntitlementService(
            IOrganizationSubscriptionRepository subscriptions,
            ISubscriptionPlanRepository plans,
            IAddOnRepository addOns,
            IMemoryCache cache,
            ILogger<EntitlementService> logger)
        {
            _subscriptions = subscriptions;
            _plans = plans;
            _addOns = addOns;
            _cache = cache;
            _logger = logger;
        }

        private static string CacheKey(string orgId) => $"entitlements:{orgId}";

        public void Invalidate(string organizationId)
        {
            if (!string.IsNullOrWhiteSpace(organizationId))
            {
                _cache.Remove(CacheKey(organizationId));
            }
        }

        public async Task<EntitlementSet> ForOrganizationAsync(string organizationId)
        {
            if (string.IsNullOrWhiteSpace(organizationId))
            {
                // No organization means no grants beyond the built-in defaults.
                return new EntitlementSet(Array.Empty<IDictionary<string, string>>());
            }

            if (_cache.TryGetValue(CacheKey(organizationId), out EntitlementSet cached))
            {
                return cached;
            }

            var layers = new List<IDictionary<string, string>>();

            var sub = await _subscriptions.GetByOrganizationIdAsync(organizationId);
            var planId = sub != null && string.Equals(sub.Status, "active", StringComparison.OrdinalIgnoreCase)
                ? sub.PlanId
                : FallbackPlanId;

            var plan = await _plans.GetByPlanIdAsync(planId);
            if (plan == null && planId != FallbackPlanId)
            {
                // An unknown or deactivated plan id must not grant more than the free tier.
                _logger.LogWarning(
                    "Organization {OrgId} references plan '{PlanId}', which does not exist or is inactive. " +
                    "Falling back to '{Fallback}'.", organizationId, planId, FallbackPlanId);
                plan = await _plans.GetByPlanIdAsync(FallbackPlanId);
            }

            if (plan != null)
            {
                layers.Add(PlanLayer(plan));
            }

            // Purchased add-ons, as their own layer. EntitlementSet sums numeric quotas across
            // layers and ORs booleans, so an add-on TOPS UP the plan rather than replacing it -
            // ten extra seats on a five-seat plan is fifteen, not ten.
            layers.AddRange(await AddOnLayersAsync(organizationId));

            var set = new EntitlementSet(layers);
            _cache.Set(CacheKey(organizationId), set, CacheFor);
            return set;
        }

        /// <summary>
        /// One layer per purchased add-on, quantity multiplied in.
        /// </summary>
        /// <remarks>
        /// A quota add-on contributes quantity x its per-unit value; a capability add-on simply
        /// grants the flag. A pure service (migration assistance) has no entitlement key and
        /// contributes nothing here, which is why it is filtered out rather than special-cased.
        ///
        /// Failure yields no layers rather than throwing: an add-on lookup that falls over must
        /// leave the customer on their plan's entitlements, never with none at all.
        /// </remarks>
        private async Task<List<IDictionary<string, string>>> AddOnLayersAsync(string organizationId)
        {
            var layers = new List<IDictionary<string, string>>();

            try
            {
                var purchases = await _addOns.GetActiveForOrganizationAsync(organizationId);
                if (purchases.Count == 0) return layers;

                var catalogue = (await _addOns.GetCatalogueAsync())
                    .Where(a => !string.IsNullOrWhiteSpace(a.AddOnId))
                    .ToDictionary(a => a.AddOnId, StringComparer.OrdinalIgnoreCase);

                foreach (var purchase in purchases)
                {
                    if (purchase.AddOnId == null
                        || !catalogue.TryGetValue(purchase.AddOnId, out var addOn)
                        || string.IsNullOrWhiteSpace(addOn.EntitlementKey)
                        || string.IsNullOrWhiteSpace(addOn.EntitlementValuePerUnit))
                    {
                        continue;
                    }

                    var quantity = Math.Max(1, purchase.Quantity);

                    var value = long.TryParse(addOn.EntitlementValuePerUnit, out var perUnit)
                        ? (perUnit * quantity).ToString()
                        : addOn.EntitlementValuePerUnit;

                    layers.Add(new Dictionary<string, string> { [addOn.EntitlementKey] = value });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Add-on entitlements unavailable for {OrgId}.", organizationId);
            }

            return layers;
        }

        /// <summary>
        /// A plan's entitlement dictionary, with UserLimit folded in as the seats quota.
        /// </summary>
        /// <remarks>
        /// UserLimit is the one entitlement that already existed and is already enforced, so it
        /// keeps its own column and is mapped in here rather than being duplicated by hand into
        /// every plan's Entitlements bag. 9999 is the sentinel the seeded "custom" plan uses for
        /// unlimited, and -1 is what the app treats as unlimited elsewhere.
        /// </remarks>
        private static IDictionary<string, string> PlanLayer(SubscriptionPlan plan)
        {
            var layer = new Dictionary<string, string>(
                plan.Entitlements ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);

            if (!layer.ContainsKey(Entitlement.Seats))
            {
                layer[Entitlement.Seats] = plan.UserLimit < 0 || plan.UserLimit >= 9999
                    ? Entitlement.Unlimited
                    : plan.UserLimit.ToString();
            }

            return layer;
        }
    }
}
