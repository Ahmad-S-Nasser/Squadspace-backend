using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>How an add-on's price scales.</summary>
    public static class AddOnUnits
    {
        /// <summary>Charged per organization member.</summary>
        public const string PerUser = "per_user";

        /// <summary>Charged per pack — 25 GB, 5,000 runs, 5 repositories.</summary>
        public const string PerPack = "per_pack";

        /// <summary>One price regardless of size.</summary>
        public const string Flat = "flat";

        /// <summary>A service, billed once. Grants no entitlement.</summary>
        public const string OneTime = "one_time";
    }

    /// <summary>
    /// One purchasable add-on.
    /// </summary>
    /// <remarks>
    /// Add-ons are what make a flat per-team price survive contact with reality. A ten-seat plan
    /// with a hard cap creates a cliff: the eleventh hire either forces a jump to the next tier or
    /// churns. An overage seat removes the cliff and keeps the pitch honest — you are buying a
    /// team price with room to grow, not a wall.
    ///
    /// Every one of these maps to an entitlement key, so the engine that already enforces plan
    /// limits enforces these too. Resolution is ADDITIVE: EntitlementSet sums numeric quotas
    /// across layers, so an add-on tops the plan up rather than replacing it. That is why the
    /// resolver was built with layers from the first commit — retrofitting additive grants onto a
    /// plan-only lookup would have meant reopening every gate.
    ///
    /// SSO is deliberately absent. Charging separately for a security control prices it away from
    /// the teams most likely to be breached, and enterprise buyers read it as a toll; it lives
    /// inside the Enterprise tier instead.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class AddOn
    {
        public string AddOnId { get; set; }

        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>A value from <see cref="AddOnUnits"/>.</summary>
        public string Unit { get; set; }

        /// <summary>What one unit covers, for display: "per user", "per 25 GB".</summary>
        public string UnitLabel { get; set; }

        public decimal MonthlyPrice { get; set; }

        /// <summary>Yearly price, or 0 when it is not sold annually.</summary>
        public decimal YearlyPrice { get; set; }

        /// <summary>Purchasable by an organization on the free plan.</summary>
        public bool AvailableOnFree { get; set; }

        /// <summary>Entitlement key this grants. Null for a pure service.</summary>
        public string EntitlementKey { get; set; }

        /// <summary>
        /// How much one unit adds to the entitlement.
        /// </summary>
        /// <remarks>
        /// A string because entitlement values are strings throughout — "true" for a capability,
        /// a number for a quota. The same idiom as the plan bag, so one parser serves both.
        /// </remarks>
        public string EntitlementValuePerUnit { get; set; }

        public int SortOrder { get; set; }
    }

    /// <summary>An add-on an organization has actually bought.</summary>
    [BsonIgnoreExtraElements]
    public class OrganizationAddOn
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string OrganizationId { get; set; }
        public string AddOnId { get; set; }

        /// <summary>How many units. Seats, packs, or 1 for a flat add-on.</summary>
        public int Quantity { get; set; } = 1;

        public string Status { get; set; } = "active";
        public string BillingCycle { get; set; } = "monthly";

        public DateTime StartDate { get; set; } = DateTime.UtcNow;
        public DateTime? EndDate { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
