using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Config
{
    /// <summary>
    /// The add-ons that can be bought on top of a plan.
    /// </summary>
    /// <remarks>
    /// Code is the source of truth, exactly like PlanCatalogue — the database holds a copy that is
    /// upserted at startup, so the catalogue moves with a deploy rather than by hand-editing rows.
    ///
    /// Yearly prices are ~20% off, matching the ratio already used on the plan ladder.
    /// </remarks>
    public static class AddOnCatalogue
    {
        public static List<AddOn> All() => new()
        {
            // AI is priced per user WITH a fair-use token allowance, plus a separate top-up,
            // because the underlying cost is per-token and unbounded. Per-seat alone loses money
            // on heavy users; pure metering is hard to sell. The pair is what the market does.
            new AddOn
            {
                AddOnId = "ai-assistant",
                Name = "AI assistant",
                Description = "Drafting, summarising and search across your workspace, with a monthly token allowance.",
                Unit = AddOnUnits.PerUser,
                UnitLabel = "per user / month",
                MonthlyPrice = 8m,
                YearlyPrice = 77m,
                AvailableOnFree = true,
                EntitlementKey = Entitlement.AiAssistant,
                EntitlementValuePerUnit = "true",
                SortOrder = 0,
            },
            new AddOn
            {
                AddOnId = "ai-tokens",
                Name = "AI usage top-up",
                Description = "An extra 1,000,000 AI tokens when the included allowance runs out.",
                Unit = AddOnUnits.PerPack,
                UnitLabel = "per 1M tokens",
                MonthlyPrice = 10m,
                YearlyPrice = 0m,
                AvailableOnFree = true,
                EntitlementKey = Entitlement.AiTokens,
                EntitlementValuePerUnit = "1000000",
                SortOrder = 1,
            },

            // The one that matters most for a flat plan: it removes the seat cliff. A team of
            // eleven on a ten-seat plan otherwise either jumps a tier or leaves. Still under
            // Jira's per-seat rate.
            new AddOn
            {
                AddOnId = "extra-seats",
                Name = "Extra seats",
                Description = "Add members beyond your plan's included seats, without changing plan.",
                Unit = AddOnUnits.PerUser,
                UnitLabel = "per user / month",
                MonthlyPrice = 6m,
                YearlyPrice = 58m,
                AvailableOnFree = false,
                EntitlementKey = Entitlement.Seats,
                EntitlementValuePerUnit = "1",
                SortOrder = 2,
            },
            new AddOn
            {
                AddOnId = "extra-storage",
                Name = "Extra storage",
                Description = "An additional 25 GB for attachments and repositories.",
                Unit = AddOnUnits.PerPack,
                UnitLabel = "per 25 GB / month",
                MonthlyPrice = 5m,
                YearlyPrice = 48m,
                AvailableOnFree = true,
                EntitlementKey = Entitlement.StorageGb,
                EntitlementValuePerUnit = "25",
                SortOrder = 3,
            },
            new AddOn
            {
                AddOnId = "automation-runs",
                Name = "Automation runs",
                Description = "An extra 500 automation rule runs a month.",
                Unit = AddOnUnits.PerPack,
                UnitLabel = "per 500 runs / month",
                MonthlyPrice = 10m,
                YearlyPrice = 96m,
                AvailableOnFree = true,
                EntitlementKey = Entitlement.AutomationRuns,
                EntitlementValuePerUnit = "500",
                SortOrder = 4,
            },
            new AddOn
            {
                AddOnId = "extra-repositories",
                Name = "Extra repositories",
                Description = "Five more Git repositories on top of your plan's allowance.",
                Unit = AddOnUnits.PerPack,
                UnitLabel = "per 5 repositories / month",
                MonthlyPrice = 8m,
                YearlyPrice = 77m,
                AvailableOnFree = false,
                EntitlementKey = Entitlement.GitRepositories,
                EntitlementValuePerUnit = "5",
                SortOrder = 5,
            },
            new AddOn
            {
                AddOnId = "priority-support",
                Name = "Priority support",
                Description = "Faster response times and a named contact.",
                Unit = AddOnUnits.Flat,
                UnitLabel = "per organization / month",
                MonthlyPrice = 49m,
                YearlyPrice = 470m,
                AvailableOnFree = false,
                EntitlementKey = Entitlement.SupportPriority,
                EntitlementValuePerUnit = "true",
                SortOrder = 6,
            },

            // A service, not an entitlement — it grants nothing, so EntitlementKey stays null.
            new AddOn
            {
                AddOnId = "migration-assistance",
                Name = "Migration assistance",
                Description = "We move your Jira, ClickUp or Azure DevOps workspace across for you.",
                Unit = AddOnUnits.OneTime,
                UnitLabel = "one-time, from",
                MonthlyPrice = 499m,
                YearlyPrice = 0m,
                AvailableOnFree = true,
                EntitlementKey = null,
                EntitlementValuePerUnit = null,
                SortOrder = 7,
            },
        };
    }
}
