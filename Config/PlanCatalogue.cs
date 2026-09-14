using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Config
{
    /// <summary>
    /// The plan catalogue: prices, seat counts and the entitlements each plan grants.
    /// </summary>
    /// <remarks>
    /// Moved out of Program.cs, where it sat as four inline one-liners in the composition
    /// root. Pricing is business data and changes far more often than startup wiring does.
    ///
    /// TWO THINGS TO KEEP IN MIND WHEN EDITING:
    ///
    /// 1. <c>Features</c> is marketing copy shown on the pricing cards; <c>Entitlements</c> is
    ///    what the server actually enforces. They are separate on purpose, but they must not
    ///    disagree — a plan whose card advertises repositories while its entitlements deny
    ///    them is a support ticket. Change both together.
    ///
    /// 2. Seats are expressed through <c>UserLimit</c>, which already existed and is already
    ///    enforced. EntitlementService maps it onto the "seats" quota, so it must not be
    ///    duplicated into the Entitlements dictionary.
    ///
    /// The seeder upserts by PlanId, so editing a plan here and restarting updates it in place
    /// on every deployment, including ones that already have the original four plans.
    /// </remarks>
    public static class PlanCatalogue
    {
        private const string Yes = "true";
        private const string No = "false";

        public static List<SubscriptionPlan> All() => new()
        {
            // Free forever, not a 30-day trial. Generous enough on people to be a real team
            // trial, capped on the things that cost money to serve.
            new SubscriptionPlan
            {
                PlanId = "free",
                Name = "Free",
                Description = "For small teams getting started. Free forever.",
                MonthlyPrice = 0, YearlyPrice = 0, Currency = "USD",
                UserLimit = 5,
                SortOrder = 1,
                Features = new List<string>
                {
                    "Up to 5 members",
                    "Projects, boards and notes",
                    "Tasks, sprints and the whiteboard",
                    "1 GB storage",
                },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.GitRepositories] = "0",
                    [Entitlement.AnalyticsAdvanced] = No,
                    [Entitlement.DataExport] = No,
                    [Entitlement.CalendarIntegration] = No,
                    [Entitlement.Sso] = No,
                    [Entitlement.StorageGb] = "1",
                    [Entitlement.AutomationRuns] = "30",
                },
            },

            new SubscriptionPlan
            {
                PlanId = "starter",
                Name = "Starter",
                Description = "For small teams and startups.",
                MonthlyPrice = 19, YearlyPrice = 182, Currency = "USD",
                UserLimit = 5,
                SortOrder = 2,
                Features = new List<string>
                {
                    "Everything in Free",
                    "Data export",
                    "10 GB storage",
                    "Email support",
                },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.DataExport] = Yes,
                    [Entitlement.GitRepositories] = "0",
                    [Entitlement.AnalyticsAdvanced] = No,
                    [Entitlement.CalendarIntegration] = No,
                    [Entitlement.Sso] = No,
                    [Entitlement.StorageGb] = "10",
                    [Entitlement.AutomationRuns] = "300",
                },
            },

            new SubscriptionPlan
            {
                PlanId = "starter-plus",
                Name = "Starter Plus",
                Description = "Same team size, more of the product.",
                MonthlyPrice = 29, YearlyPrice = 278, Currency = "USD",
                UserLimit = 5,
                SortOrder = 3,
                Features = new List<string>
                {
                    "Everything in Starter",
                    "Git repositories",
                    "Calendar invitations",
                    "25 GB storage",
                },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.DataExport] = Yes,
                    [Entitlement.GitRepositories] = "5",
                    [Entitlement.CalendarIntegration] = Yes,
                    [Entitlement.AnalyticsAdvanced] = No,
                    [Entitlement.Sso] = No,
                    [Entitlement.StorageGb] = "25",
                    [Entitlement.AutomationRuns] = "500",
                },
            },

            new SubscriptionPlan
            {
                PlanId = "pro",
                Name = "Pro",
                Description = "For medium-sized teams.",
                MonthlyPrice = 49, YearlyPrice = 470, Currency = "USD",
                UserLimit = 10,
                SortOrder = 4,
                Features = new List<string>
                {
                    "Everything in Starter Plus",
                    "Up to 10 members",
                    "50 GB storage",
                },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.DataExport] = Yes,
                    [Entitlement.GitRepositories] = "20",
                    [Entitlement.CalendarIntegration] = Yes,
                    [Entitlement.AnalyticsAdvanced] = No,
                    [Entitlement.Sso] = No,
                    [Entitlement.StorageGb] = "50",
                    [Entitlement.AutomationRuns] = "1000",
                },
            },

            new SubscriptionPlan
            {
                PlanId = "pro-plus",
                Name = "Pro Plus",
                Description = "Same team size, with the reporting a manager wants.",
                MonthlyPrice = 79, YearlyPrice = 758, Currency = "USD",
                UserLimit = 10,
                SortOrder = 5,
                Features = new List<string>
                {
                    "Everything in Pro",
                    "Advanced analytics and reports",
                    "Unlimited repositories",
                    "100 GB storage",
                },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.DataExport] = Yes,
                    [Entitlement.GitRepositories] = Entitlement.Unlimited,
                    [Entitlement.CalendarIntegration] = Yes,
                    [Entitlement.AnalyticsAdvanced] = Yes,
                    [Entitlement.Sso] = No,
                    [Entitlement.StorageGb] = "100",
                    [Entitlement.AutomationRuns] = "2000",
                },
            },

            new SubscriptionPlan
            {
                PlanId = "business",
                Name = "Business",
                Description = "For large teams.",
                MonthlyPrice = 149, YearlyPrice = 1430, Currency = "USD",
                UserLimit = 25,
                SortOrder = 6,
                Features = new List<string>
                {
                    "Everything in Pro Plus",
                    "Up to 25 members",
                    "250 GB storage",
                    "Priority support",
                },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.DataExport] = Yes,
                    [Entitlement.GitRepositories] = Entitlement.Unlimited,
                    [Entitlement.CalendarIntegration] = Yes,
                    [Entitlement.AnalyticsAdvanced] = Yes,
                    [Entitlement.Sso] = No,
                    [Entitlement.StorageGb] = "250",
                    [Entitlement.AutomationRuns] = Entitlement.Unlimited,
                },
            },

            // Renamed from "custom": that described the contract, not the buyer. The PlanId is
            // kept as "custom" so existing subscriptions keep resolving.
            new SubscriptionPlan
            {
                PlanId = "custom",
                Name = "Enterprise",
                Description = "For organizations that need SSO, contracts and control.",
                MonthlyPrice = -1, YearlyPrice = -1, Currency = "USD",   // -1 = contact sales
                UserLimit = 9999,                                        // 9999 = unlimited
                SortOrder = 7,
                Features = new List<string>
                {
                    "Everything in Business",
                    "Unlimited members",
                    "SSO and advanced security",
                    "Custom contracts",
                    "On-premise option",
                },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.DataExport] = Yes,
                    [Entitlement.GitRepositories] = Entitlement.Unlimited,
                    [Entitlement.CalendarIntegration] = Yes,
                    [Entitlement.AnalyticsAdvanced] = Yes,
                    // SSO is included here rather than sold as an add-on: charging separately
                    // for a security control prices it out of reach of the teams that most
                    // need it, and enterprise buyers read it as a toll.
                    [Entitlement.Sso] = Yes,
                    [Entitlement.StorageGb] = Entitlement.Unlimited,
                    [Entitlement.AutomationRuns] = Entitlement.Unlimited,
                },
            },

            // Retired. Kept ACTIVE-FALSE rather than deleted so that any organization still
            // pointing at it resolves to a real plan instead of falling back to free, and so
            // its historic subscriptions still render a name in the billing UI.
            new SubscriptionPlan
            {
                PlanId = "personal",
                Name = "Personal (retired)",
                Description = "Replaced by the Free plan.",
                MonthlyPrice = 0, YearlyPrice = 0, Currency = "USD",
                UserLimit = 1,
                SortOrder = 99,
                IsActive = false,
                Features = new List<string> { "Replaced by Free" },
                Entitlements = new Dictionary<string, string>
                {
                    [Entitlement.Whiteboard] = Yes,
                    [Entitlement.Sprints] = Yes,
                    [Entitlement.Meetings] = Yes,
                    [Entitlement.StorageGb] = "1",
                },
            },
        };
    }
}
