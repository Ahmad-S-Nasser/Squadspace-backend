using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>
    /// The background job runner. Polls for due scheduled rules and executes them.
    /// </summary>
    /// <remarks>
    /// THE FIRST BACKGROUND WORKER IN THIS CODEBASE. There was no Hangfire, no Quartz, and no
    /// IHostedService of any kind - which is why several earlier features are shaped the way they
    /// are: the runaway-timer cap in Phase 3 has to be applied lazily on read because nothing
    /// could sweep for it, and subscription expiry is likewise only noticed when someone asks.
    /// This pays for itself beyond automation.
    ///
    /// Deliberately a poll loop over a Mongo query rather than a job framework. Adding Hangfire
    /// would bring its own storage, dashboard and schema into a codebase that has no migrations
    /// story beyond the one built in Phase 2; a minute-resolution poll over an indexed NextRunAt
    /// is enough for cadences measured in hours and days, and it is auditable in one file.
    ///
    /// SAFE ON SEVERAL INSTANCES. Every instance polls, but a rule is taken with an atomic
    /// find-and-update that leases it - see ClaimDueRuleAsync. Without that, four app instances
    /// would each send the same Monday report.
    ///
    /// It never throws. An unhandled exception in a BackgroundService stops the host in .NET 8,
    /// so a single malformed rule would take the whole API down with it.
    /// </remarks>
    public class AutomationHostedService : BackgroundService
    {
        /// <summary>Cadences are hours and days; a minute of latency is invisible.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

        /// <summary>How long a claimed rule stays claimed. Longer than any action should take.</summary>
        private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

        /// <summary>Ceiling per tick, so one busy minute cannot monopolise the loop.</summary>
        private const int MaxPerTick = 25;

        /// <summary>After this many consecutive failures a rule is disabled rather than retried forever.</summary>
        private const int FailureLimit = 5;

        private readonly IServiceProvider _services;
        private readonly ILogger<AutomationHostedService> _logger;

        public AutomationHostedService(IServiceProvider services, ILogger<AutomationHostedService> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app finish starting before competing with it for the database.
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { return; }

            _logger.LogInformation("Automation runner started; polling every {Seconds}s.", PollInterval.TotalSeconds);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never propagate: an unhandled exception here would stop the host.
                    _logger.LogError(ex, "Automation tick failed.");
                }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("Automation runner stopped.");
        }

        private async Task TickAsync(CancellationToken stoppingToken)
        {
            var automation = _services.GetService<IAutomationRepository>();
            var engine = _services.GetService<IAutomationEngine>();

            if (automation == null || engine == null) return;

            for (var i = 0; i < MaxPerTick && !stoppingToken.IsCancellationRequested; i++)
            {
                var now = DateTime.UtcNow;

                var rule = await automation.ClaimDueRuleAsync(now, Lease);
                if (rule == null) return;

                AutomationOutcome outcome;
                try
                {
                    outcome = await engine.ExecuteAsync(new AutomationContext { Rule = rule });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Automation rule {RuleId} threw.", rule.Id);
                    outcome = AutomationOutcome.Fail(ex.Message);
                }

                await ReschedulAsync(automation, engine, rule, outcome, now);
            }
        }

        /// <summary>
        /// Books the rule's next occurrence and releases the lease.
        /// </summary>
        /// <remarks>
        /// NextRunAt is computed from NOW rather than from the previous due time. Computing it
        /// from the schedule would make a rule that was down for a day fire every missed
        /// occurrence in a burst the moment it came back - which for an email report means the
        /// customer gets a day's worth at once.
        ///
        /// A rule that keeps failing is disabled rather than retried forever: a broken rule
        /// sending a broken email every hour is worse than one that stopped and said so.
        /// </remarks>
        private async Task ReschedulAsync(
            IAutomationRepository automation,
            IAutomationEngine engine,
            AutomationRule rule,
            AutomationOutcome outcome,
            DateTime now)
        {
            rule.LastRunAt = now;
            rule.LastRunState = outcome.State;
            rule.LockedUntil = null;

            if (outcome.State == AutomationRunStates.Failed)
            {
                rule.ConsecutiveFailures++;

                if (rule.ConsecutiveFailures >= FailureLimit)
                {
                    rule.Enabled = false;
                    _logger.LogWarning(
                        "Automation rule {RuleId} disabled after {Count} consecutive failures.",
                        rule.Id, rule.ConsecutiveFailures);
                }
            }
            else
            {
                rule.ConsecutiveFailures = 0;
            }

            rule.NextRunAt = engine.ComputeNextRun(rule, now);
            await automation.UpsertAsync(rule);
        }
    }
}
