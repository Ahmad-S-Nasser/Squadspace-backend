using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// What the product does and what it costs. Public, for the marketing site.
    /// </summary>
    /// <remarks>
    /// ANONYMOUS ON PURPOSE, and safe to be: it serves only the published catalogue - the same
    /// feature list, plan ladder and add-on prices any visitor sees on the pricing page. There is
    /// no organization, user or usage data on any of these routes.
    ///
    /// It exists so the website stops keeping its own copy of the feature list. That copy is what
    /// let the pricing page claim "nothing is locked behind a higher plan" while the entitlement
    /// engine gated repositories, analytics and export - a hand-maintained list in the one place
    /// prospects actually read is the one guaranteed to drift.
    ///
    /// Per-tier inclusion is DERIVED from each plan's own entitlement bag, so a feature cannot be
    /// advertised on a tier that does not grant it.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    [AllowAnonymous]
    public class CatalogController : ControllerBase
    {
        private readonly ISubscriptionPlanRepository _plans;
        private readonly IAddOnRepository _addOns;

        public CatalogController(ISubscriptionPlanRepository plans, IAddOnRepository addOns)
        {
            _plans = plans;
            _addOns = addOns;
        }

        /// <summary>Every feature, grouped by category, with the entitlement that gates it.</summary>
        /// <param name="lang">
        /// "ar" for Arabic; anything else returns English.
        /// </param>
        /// <remarks>
        /// The response SHAPE does not change with the language - only the text inside `name`
        /// and `description`. Clients therefore need no branching, and a page that forgets the
        /// parameter degrades to English rather than breaking.
        ///
        /// This exists because feature names are the words people search for. The Arabic
        /// marketing site listed all 34 of them in English, which made it unfindable in Arabic
        /// on the terms it most needed.
        /// </remarks>
        [HttpGet("features")]
        public IActionResult Features([FromQuery] string lang = null)
        {
            var features = FeatureCatalogueAr
                .Localize(FeatureCatalogue.All(), lang)
                .OrderBy(f => f.SortOrder)
                .ToList();

            return Ok(new
            {
                categories = features
                    .GroupBy(f => f.Category)
                    .Select(g => new
                    {
                        name = g.Key,
                        features = g.Select(f => new
                        {
                            key = f.Key,
                            name = f.Name,
                            description = f.Description,
                            entitlementKey = f.EntitlementKey,

                            // No gate means every plan, including free.
                            includedEverywhere = string.IsNullOrEmpty(f.EntitlementKey),
                        }),
                    }),
                total = features.Count,
            });
        }

        /// <summary>The add-ons sold on top of a plan.</summary>
        [HttpGet("addons")]
        public async Task<IActionResult> AddOns()
        {
            var addOns = await _addOns.GetCatalogueAsync();

            return Ok(addOns.Select(a => new
            {
                id = a.AddOnId,
                name = a.Name,
                description = a.Description,
                unit = a.Unit,
                unitLabel = a.UnitLabel,
                monthlyPrice = a.MonthlyPrice,
                yearlyPrice = a.YearlyPrice,
                availableOnFree = a.AvailableOnFree,
                entitlementKey = a.EntitlementKey,
                sortOrder = a.SortOrder,
            }));
        }

        /// <summary>
        /// Which features each plan actually includes, derived from its entitlements.
        /// </summary>
        /// <remarks>
        /// The comparison table behind the pricing page. Because inclusion is read from the same
        /// entitlement bag the server enforces at runtime, the table cannot claim something the
        /// product would refuse - which is the failure this endpoint exists to prevent.
        /// </remarks>
        [HttpGet("comparison")]
        public async Task<IActionResult> Comparison([FromQuery] string lang = null)
        {
            var plans = await _plans.GetAllActiveAsync();
            var features = FeatureCatalogueAr
                .Localize(FeatureCatalogue.All(), lang)
                .OrderBy(f => f.SortOrder)
                .ToList();

            var rows = plans
                // "personal" is retired but kept active so existing subscriptions still
                // resolve; it must not appear on the pricing page.
                .Where(p => p.PlanId != "personal")
                .OrderBy(p => p.MonthlyPrice < 0 ? decimal.MaxValue : p.MonthlyPrice)
                .Select(plan =>
                {
                    var entitlements = plan.Entitlements ?? new Dictionary<string, string>();

                    return new
                    {
                        planId = plan.PlanId,
                        name = plan.Name,
                        features = features.ToDictionary(
                            f => f.Key,
                            f => Describe(entitlements, f.EntitlementKey)),
                    };
                });

            return Ok(new
            {
                features = features.Select(f => new { key = f.Key, name = f.Name, category = f.Category }),
                plans = rows,
            });
        }

        /// <summary>
        /// What a plan grants for one feature: "yes", "no", or the quota.
        /// </summary>
        /// <remarks>
        /// A quota of zero reads as "no", not as "0" - a tier that allows none of something does
        /// not include it, and printing a bare zero in a comparison table invites the reader to
        /// think it is merely a small allowance.
        /// </remarks>
        private static string Describe(IDictionary<string, string> entitlements, string key)
        {
            if (string.IsNullOrEmpty(key)) return "yes";
            if (!entitlements.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return "no";

            if (string.Equals(raw, Entitlement.Unlimited, StringComparison.OrdinalIgnoreCase)) return "unlimited";
            if (bool.TryParse(raw, out var flag)) return flag ? "yes" : "no";
            if (long.TryParse(raw, out var number)) return number <= 0 ? "no" : number.ToString();

            return raw;
        }
    }
}
