using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>What the buyer sends. Note the absence of a price.</summary>
    /// <remarks>
    /// There is no Price field and there never should be. The server prices the order from the
    /// catalogue; a price in the body is a number the buyer chose.
    /// </remarks>
    public class PurchaseAddOnRequest
    {
        public string AddOnId { get; set; }
        public int Quantity { get; set; } = 1;
        public string BillingCycle { get; set; } = "monthly";

        /// <summary>Where to send the buyer back to after the gateway. Validated against an allowlist.</summary>
        public string ReturnUrl { get; set; }
    }

    /// <summary>An approval or rejection from a platform admin.</summary>
    public class AddOnDecisionRequest
    {
        public string Note { get; set; }
    }

    /// <summary>
    /// Buying, listing and cancelling add-ons.
    /// </summary>
    /// <remarks>
    /// Buying is MANAGERS ONLY. It commits the organization to money, which is not something an
    /// ordinary member or a viewer gets to do on everyone else's behalf. Seeing what the
    /// organization already has is open to members, because an entitlement your team cannot see
    /// is one they will keep asking you about.
    ///
    /// Approving an order is PLATFORM ADMIN only, and that is a different axis entirely: it is
    /// the vendor deciding to grant something that was not paid for by card. An organization
    /// admin approving their own request would be a free add-on button.
    /// </remarks>
    [Route("api/organization/{orgId}/addons")]
    [ApiController]
    [Authorize]
    public class AddOnsController : ControllerBase
    {
        private readonly IAddOnRepository _addOns;
        private readonly IPaymentOrderRepository _orders;
        private readonly IOrganizationRepository _organizations;
        private readonly IContributorRepository _contributors;
        private readonly INotificationRepository _notifications;
        private readonly IAddOnPurchaseService _purchases;
        private readonly IEntitlementService _entitlements;
        private readonly IConfiguration _config;
        private readonly ILogger<AddOnsController> _logger;

        public AddOnsController(
            IAddOnRepository addOns,
            IPaymentOrderRepository orders,
            IOrganizationRepository organizations,
            IContributorRepository contributors,
            INotificationRepository notifications,
            IAddOnPurchaseService purchases,
            IEntitlementService entitlements,
            IConfiguration config,
            ILogger<AddOnsController> logger)
        {
            _addOns = addOns;
            _orders = orders;
            _organizations = organizations;
            _contributors = contributors;
            _notifications = notifications;
            _purchases = purchases;
            _entitlements = entitlements;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// The catalogue, priced, with what this organization already owns marked.
        /// </summary>
        /// <remarks>
        /// One call rather than making the client fetch the catalogue and the purchases and join
        /// them: the join needs the pricing rules, and a client that does it itself will get the
        /// yearly-price-of-zero case wrong.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> Catalogue(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var catalogue = await _addOns.GetCatalogueAsync();
            var owned = await _addOns.GetActiveForOrganizationAsync(orgId);

            var ownedByAddOn = owned
                .Where(o => !string.IsNullOrWhiteSpace(o.AddOnId))
                .GroupBy(o => o.AddOnId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(o => o.Quantity), StringComparer.OrdinalIgnoreCase);

            return Ok(new
            {
                checkoutAvailable = _purchases.CheckoutAvailable,
                currency = (_config["Payments:Currency"] ?? "USD").ToUpperInvariant(),
                canPurchase = OrgAccess.Managers.Contains(auth.Role ?? string.Empty),
                addOns = catalogue.Select(a => new
                {
                    a.AddOnId,
                    a.Name,
                    a.Description,
                    a.Unit,
                    a.UnitLabel,
                    a.MonthlyPrice,
                    a.YearlyPrice,
                    a.AvailableOnFree,
                    a.SortOrder,
                    ownedQuantity = ownedByAddOn.TryGetValue(a.AddOnId ?? string.Empty, out var q) ? q : 0,
                }),
            });
        }

        /// <summary>What the organization currently owns.</summary>
        [HttpGet("mine")]
        public async Task<IActionResult> Mine(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(await _addOns.GetActiveForOrganizationAsync(orgId));
        }

        /// <summary>The organization's order history.</summary>
        [HttpGet("orders")]
        public async Task<IActionResult> Orders(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            return Ok(await _orders.GetForOrganizationAsync(orgId));
        }

        /// <summary>
        /// Starts a purchase: either a checkout redirect, or a request awaiting approval.
        /// </summary>
        [HttpPost("purchase")]
        public async Task<IActionResult> Purchase(string orgId, [FromBody] PurchaseAddOnRequest request)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            if (request == null) return BadRequest(new { message = "A request body is required." });

            var buyer = await _contributors.GetByIdAsync(auth.UserId);

            var start = await _purchases.StartAsync(
                organizationId: orgId,
                addOnId: request.AddOnId,
                quantity: request.Quantity,
                billingCycle: request.BillingCycle,
                userId: auth.UserId,
                userName: buyer?.Name,
                userEmail: buyer?.Email,
                returnUrlBase: SafeReturnUrl(request.ReturnUrl),
                webhookUrl: WebhookUrl());

            if (!start.Ok) return BadRequest(new { message = start.Problem });

            if (start.NeedsApproval)
            {
                await NotifyApprovalNeededAsync(start.Order, auth.Org?.Name);

                return Ok(new
                {
                    orderId = start.Order.Id,
                    status = start.Order.Status,
                    needsApproval = true,
                    amount = start.Order.Amount,
                    currency = start.Order.Currency,
                    message = "Request received. We will set this up on your account and confirm by email.",
                });
            }

            return Ok(new
            {
                orderId = start.Order.Id,
                status = start.Order.Status,
                needsApproval = false,
                amount = start.Order.Amount,
                currency = start.Order.Currency,
                checkoutUrl = start.CheckoutUrl,
            });
        }

        /// <summary>Withdraws an order that has not been paid.</summary>
        [HttpPost("orders/{orderId}/cancel")]
        public async Task<IActionResult> CancelOrder(string orgId, string orderId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var order = await _orders.GetByIdAsync(orderId);

            // Tenancy is re-derived from the STORED order, never from the route. Without this a
            // manager of one organization could cancel another organization's order by guessing
            // an id, since the route's orgId is only checked against their own membership.
            if (order == null || !string.Equals(order.OrganizationId, orgId, StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(new { message = "No such order." });
            }

            if (!PaymentOrderStatus.Open.Contains(order.Status))
            {
                return BadRequest(new { message = $"That order is already {order.Status}." });
            }

            await _purchases.CloseUnfulfilledAsync(order, PaymentOrderStatus.Cancelled, "Withdrawn by the buyer.");
            return Ok(new { cancelled = true });
        }

        /// <summary>
        /// Turns off an add-on the organization owns.
        /// </summary>
        /// <remarks>
        /// Marked inactive rather than deleted, so the billing history stays intact and the grant
        /// stops counting immediately - GetActiveForOrganizationAsync filters on status.
        /// </remarks>
        [HttpPost("cancel/{purchaseId}")]
        public async Task<IActionResult> CancelAddOn(string orgId, string purchaseId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var owned = await _addOns.GetActiveForOrganizationAsync(orgId);
            var grant = owned.FirstOrDefault(o => o.Id == purchaseId);

            if (grant == null) return NotFound(new { message = "No such add-on on this organization." });

            grant.Status = "cancelled";
            grant.EndDate = DateTime.UtcNow;
            await _addOns.UpsertPurchaseAsync(grant);

            _entitlements.Invalidate(orgId);

            _logger.LogInformation(
                "Add-on {AddOnId} cancelled on org {OrgId} by {UserId}.", grant.AddOnId, orgId, auth.UserId);

            return Ok(new { cancelled = true });
        }

        // ---------------------------------------------------------------------------------
        // Vendor side. Platform admins only.
        // ---------------------------------------------------------------------------------

        /// <summary>Orders waiting on a human. Platform admins only.</summary>
        [HttpGet("/api/addons/pending")]
        public async Task<IActionResult> Pending()
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return NotFound();

            return Ok(await _orders.GetByStatusAsync(new[] { PaymentOrderStatus.PendingApproval }));
        }

        /// <summary>Grants an unpaid request. Platform admins only.</summary>
        /// <remarks>
        /// Goes through the same fulfilment path as a verified webhook, so an approved request
        /// and a paid one produce identical state. The provider is recorded as "manual" - an
        /// entitlement granted by hand must be distinguishable from one that was paid for, both
        /// for reconciliation and for the conversation when the customer asks for an invoice.
        /// </remarks>
        [HttpPost("/api/addons/{orderId}/approve")]
        public async Task<IActionResult> Approve(string orderId, [FromBody] AddOnDecisionRequest decision)
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return NotFound();

            var order = await _orders.GetByIdAsync(orderId);
            if (order == null) return NotFound(new { message = "No such order." });

            if (!PaymentOrderStatus.Open.Contains(order.Status))
            {
                return BadRequest(new { message = $"That order is already {order.Status}." });
            }

            var granted = await _purchases.FulfilAsync(
                order, "manual", null,
                string.IsNullOrWhiteSpace(decision?.Note)
                    ? $"Approved by {OrgAccess.Email(User)}."
                    : decision.Note);

            return Ok(new { granted });
        }

        /// <summary>Turns down an unpaid request. Platform admins only.</summary>
        [HttpPost("/api/addons/{orderId}/reject")]
        public async Task<IActionResult> Reject(string orderId, [FromBody] AddOnDecisionRequest decision)
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return NotFound();

            var order = await _orders.GetByIdAsync(orderId);
            if (order == null) return NotFound(new { message = "No such order." });

            if (!PaymentOrderStatus.Open.Contains(order.Status))
            {
                return BadRequest(new { message = $"That order is already {order.Status}." });
            }

            await _purchases.CloseUnfulfilledAsync(
                order, PaymentOrderStatus.Cancelled,
                string.IsNullOrWhiteSpace(decision?.Note)
                    ? $"Declined by {OrgAccess.Email(User)}."
                    : decision.Note);

            return Ok(new { rejected = true });
        }

        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Keeps the post-payment return on a host we control.
        /// </summary>
        /// <remarks>
        /// The return URL arrives in a request body and ends up somewhere a paying customer is
        /// redirected to, which is the shape of an open-redirect: a crafted purchase would send
        /// the buyer to an attacker's page at the moment they are most primed to enter card
        /// details. Anything not on the allowlist falls back to the configured app URL rather
        /// than being rejected, because the buyer should not lose their purchase over it.
        /// </remarks>
        private string SafeReturnUrl(string requested)
        {
            var fallback = _config["App:BaseUrl"] ?? "https://app.squadspace.net";

            if (string.IsNullOrWhiteSpace(requested)) return fallback;

            if (!Uri.TryCreate(requested, UriKind.Absolute, out var uri)) return fallback;
            if (uri.Scheme != Uri.UriSchemeHttps) return fallback;

            var allowed = (_config["App:AllowedReturnHosts"] ?? "app.squadspace.net,squadspace.net,www.squadspace.net")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var ok = allowed.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));

            if (!ok)
            {
                _logger.LogWarning("Rejected an off-host return URL: {Host}", uri.Host);
                return fallback;
            }

            return requested;
        }

        /// <summary>Absolute URL the gateway posts the notification to.</summary>
        private string WebhookUrl()
        {
            var configured = _config["Payments:WebhookUrl"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            // Derived from the current request so a dev machine works without configuration.
            // Production should set Payments:WebhookUrl explicitly - behind a proxy the scheme
            // and host here are whatever the proxy forwarded, which is not a thing to hand a
            // payment provider on trust.
            return $"{Request.Scheme}://{Request.Host}/api/payments/webhook/kashier";
        }

        private async Task NotifyApprovalNeededAsync(PaymentOrder order, string orgName)
        {
            try
            {
                var inbox = _config["Feedback:NotifyEmail"];
                if (string.IsNullOrWhiteSpace(inbox)) return;

                var response = await _notifications.SendEmailNotificationAsync(new NotificationRequest
                {
                    Type = "addon_request",
                    TaskTitle = order.AddOnName,
                    RecipientEmails = new List<string> { inbox },
                    Metadata = new Dictionary<string, string>
                    {
                        { "OrderId", order.Id ?? string.Empty },
                        { "AddOn", order.AddOnName ?? string.Empty },
                        { "Quantity", order.Quantity.ToString() },
                        { "BillingCycle", order.BillingCycle ?? string.Empty },
                        { "Amount", $"{order.Amount:0.00} {order.Currency}" },
                        { "Organization", $"{orgName} ({order.OrganizationId})" },
                        { "From", $"{order.RequestedByName} <{order.RequestedByEmail}>" },
                    },
                });

                // SendEmailNotificationAsync reports failure in the RESULT rather than throwing,
                // so without this check a broken inbox means add-on requests pile up unseen.
                if (response is not { Success: true })
                {
                    _logger.LogWarning(
                        "Add-on request {OrderId} saved but the notification to {Inbox} failed: {Reason}",
                        order.Id, inbox, response?.Message ?? "no response");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Add-on request {OrderId} saved but could not be emailed.", order.Id);
            }
        }
    }
}
