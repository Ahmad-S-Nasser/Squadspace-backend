using Hxl.Payments;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>The outcome of starting a purchase.</summary>
    public sealed class PurchaseStart
    {
        public bool Ok { get; init; }

        /// <summary>Why it was refused. Safe to show the buyer.</summary>
        public string Problem { get; init; }

        public PaymentOrder Order { get; init; }

        /// <summary>Where to send the buyer to pay. Null on the approval route.</summary>
        public string CheckoutUrl { get; init; }

        /// <summary>True when there is no gateway and a human has to approve instead.</summary>
        public bool NeedsApproval { get; init; }

        public static PurchaseStart Refuse(string problem) => new() { Ok = false, Problem = problem };
    }

    public interface IAddOnPurchaseService
    {
        /// <summary>Whether a card can actually be taken right now.</summary>
        bool CheckoutAvailable { get; }

        Task<PurchaseStart> StartAsync(
            string organizationId, string addOnId, int quantity, string billingCycle,
            string userId, string userName, string userEmail, string returnUrlBase, string webhookUrl);

        /// <summary>
        /// Grants the add-on and closes the order. Safe to call twice.
        /// </summary>
        /// <returns>True when this call is the one that granted it.</returns>
        Task<bool> FulfilAsync(PaymentOrder order, string provider, string providerReference, string note);

        /// <summary>Closes an order without granting anything.</summary>
        Task CloseUnfulfilledAsync(PaymentOrder order, string status, string note);

        /// <summary>Takes back what a refunded order granted, and marks it refunded.</summary>
        Task RevokeAsync(PaymentOrder order);
    }

    /// <summary>
    /// Turns "I want this add-on" into money owed and, once paid, into entitlement.
    /// </summary>
    /// <remarks>
    /// The single reason this is a service rather than controller code: fulfilment must be
    /// IDENTICAL whether it was triggered by a verified webhook or by a platform admin approving
    /// a manual request. Two copies of "create the grant, close the order, invalidate the cache"
    /// would eventually disagree, and the way you find out is a customer who paid and got
    /// nothing.
    ///
    /// Pricing is computed here, from the catalogue, every time. A price that arrives in a
    /// request body is a price the buyer chose.
    /// </remarks>
    public class AddOnPurchaseService : IAddOnPurchaseService
    {
        /// <summary>Enough for a large team; small enough that a typo cannot invoice a fortune.</summary>
        private const int MaxQuantity = 500;

        private readonly IAddOnRepository _addOns;
        private readonly IPaymentOrderRepository _orders;
        private readonly IEntitlementService _entitlements;
        private readonly IPaymentGateway _gateway;
        private readonly IConfiguration _config;
        private readonly ILogger<AddOnPurchaseService> _logger;

        public AddOnPurchaseService(
            IAddOnRepository addOns,
            IPaymentOrderRepository orders,
            IEntitlementService entitlements,
            IPaymentGateway gateway,
            IConfiguration config,
            ILogger<AddOnPurchaseService> logger)
        {
            _addOns = addOns;
            _orders = orders;
            _entitlements = entitlements;
            _gateway = gateway;
            _config = config;
            _logger = logger;
        }

        public bool CheckoutAvailable => _gateway.IsConfigured;

        /// <summary>
        /// The currency charged in.
        /// </summary>
        /// <remarks>
        /// Configured, not derived. The catalogue is written in USD and an Egyptian merchant
        /// account may only settle EGP - and the honest way to handle that is to set the currency
        /// and the prices deliberately, not to have the API multiply by a rate it invented.
        /// </remarks>
        private string Currency =>
            (_config["Payments:Currency"] ?? "USD").Trim().ToUpperInvariant();

        public async Task<PurchaseStart> StartAsync(
            string organizationId, string addOnId, int quantity, string billingCycle,
            string userId, string userName, string userEmail, string returnUrlBase, string webhookUrl)
        {
            if (string.IsNullOrWhiteSpace(addOnId))
            {
                return PurchaseStart.Refuse("No add-on was specified.");
            }

            if (quantity < 1 || quantity > MaxQuantity)
            {
                return PurchaseStart.Refuse($"Quantity must be between 1 and {MaxQuantity}.");
            }

            var cycle = string.Equals(billingCycle, "yearly", StringComparison.OrdinalIgnoreCase)
                ? "yearly"
                : "monthly";

            var addOn = (await _addOns.GetCatalogueAsync())
                .FirstOrDefault(a => string.Equals(a.AddOnId, addOnId, StringComparison.OrdinalIgnoreCase));

            if (addOn == null)
            {
                return PurchaseStart.Refuse("That add-on does not exist.");
            }

            var unitPrice = cycle == "yearly" ? addOn.YearlyPrice : addOn.MonthlyPrice;

            // A zero yearly price in the catalogue means "not sold annually", not "free".
            if (unitPrice <= 0)
            {
                return PurchaseStart.Refuse(cycle == "yearly"
                    ? "This add-on is not sold annually."
                    : "This add-on has no price and cannot be bought here.");
            }

            var order = new PaymentOrder
            {
                OrganizationId = organizationId,
                RequestedByUserId = userId,
                RequestedByName = userName,
                RequestedByEmail = userEmail,
                AddOnId = addOn.AddOnId,
                AddOnName = addOn.Name,
                Quantity = quantity,
                BillingCycle = cycle,
                UnitPrice = unitPrice,
                Amount = unitPrice * quantity,
                Currency = Currency,
            };

            // No gateway: record the request and let a human approve it. This is a real state,
            // not a stub - the order is a genuine record and approving it grants exactly what a
            // paid one would.
            if (!_gateway.IsConfigured)
            {
                order.Status = PaymentOrderStatus.PendingApproval;
                order.Provider = "manual";
                await _orders.CreateAsync(order);

                _logger.LogInformation(
                    "Add-on request {OrderId} ({AddOn} x{Quantity}) for org {OrgId} awaits approval; no gateway configured.",
                    order.Id, order.AddOnId, order.Quantity, organizationId);

                return new PurchaseStart { Ok = true, Order = order, NeedsApproval = true };
            }

            order.Status = PaymentOrderStatus.PendingPayment;
            order.Provider = _gateway.Name;

            // Written BEFORE the redirect is handed out. If it were written after, a buyer who
            // paid against an order the database never heard of would produce a webhook with
            // nothing to match, and the only trace would be in the gateway's dashboard.
            await _orders.CreateAsync(order);

            try
            {
                var session = await _gateway.CreateCheckoutAsync(new CheckoutRequest
                {
                    OrderId = order.Id,
                    Amount = order.Amount,
                    Currency = order.Currency,
                    Description = $"{addOn.Name} x{quantity} ({cycle})",
                    CustomerEmail = userEmail,
                    CustomerName = userName,
                    SuccessUrl = Combine(returnUrlBase, $"?order={order.Id}&result=success"),
                    FailureUrl = Combine(returnUrlBase, $"?order={order.Id}&result=failed"),
                    WebhookUrl = webhookUrl,
                    Metadata = new Dictionary<string, string>
                    {
                        ["organizationId"] = organizationId ?? string.Empty,
                        ["addOnId"] = addOn.AddOnId ?? string.Empty,
                    },
                });

                return new PurchaseStart { Ok = true, Order = order, CheckoutUrl = session.RedirectUrl };
            }
            catch (Exception ex)
            {
                // The order stays on the record as cancelled rather than being deleted: a failed
                // checkout attempt is information, and deleting rows to tidy up an audit trail is
                // how you lose the evidence for the bug you are about to investigate.
                _logger.LogError(ex, "Could not start checkout for order {OrderId}.", order.Id);
                await CloseUnfulfilledAsync(order, PaymentOrderStatus.Cancelled, "Checkout could not be started.");

                return PurchaseStart.Refuse("Could not start checkout. Please try again shortly.");
            }
        }

        public async Task<bool> FulfilAsync(
            PaymentOrder order, string provider, string providerReference, string note)
        {
            if (order == null) return false;

            // Already settled. Not an error - a gateway retrying a webhook it did not get a 200
            // for is normal, and the correct response is to do nothing and say yes.
            if (order.Status == PaymentOrderStatus.Paid)
            {
                _logger.LogInformation("Order {OrderId} is already paid; ignoring a repeat notification.", order.Id);
                return false;
            }

            var grant = new OrganizationAddOn
            {
                OrganizationId = order.OrganizationId,
                AddOnId = order.AddOnId,
                Quantity = order.Quantity,
                BillingCycle = order.BillingCycle,
                Status = "active",
                StartDate = DateTime.UtcNow,

                // No EndDate: this is a running subscription, and it stops when it is cancelled
                // or a renewal is missed. Setting one here would silently expire a paying
                // customer's add-on a month later with nothing to renew it.
                EndDate = null,
            };

            // Close FIRST, and only grant if this call is the one that closed it. Doing it the
            // other way round means two concurrent webhooks both create a grant and only then
            // discover one of them lost - by which point the organization has two.
            var won = await _orders.TryCloseAsync(
                order.Id, PaymentOrderStatus.Paid, providerReference, grant.Id, note);

            if (!won)
            {
                _logger.LogInformation(
                    "Order {OrderId} was closed by another delivery; not granting again.", order.Id);
                return false;
            }

            await _addOns.UpsertPurchaseAsync(grant);

            // Without this the customer pays and sees nothing change until the cache expires.
            _entitlements.Invalidate(order.OrganizationId);

            _logger.LogInformation(
                "Order {OrderId} fulfilled: {AddOn} x{Quantity} granted to org {OrgId} via {Provider}.",
                order.Id, order.AddOnId, order.Quantity, order.OrganizationId, provider);

            return true;
        }

        public async Task CloseUnfulfilledAsync(PaymentOrder order, string status, string note)
        {
            if (order == null) return;
            await _orders.TryCloseAsync(order.Id, status, null, null, note);
        }

        /// <remarks>
        /// Revoking has to work on an order that is already CLOSED - it is refunding something
        /// that was paid - so it cannot go through TryCloseAsync, which by design only moves open
        /// orders. The grant is looked up by the id recorded on the order rather than by add-on
        /// type, so refunding one purchase of extra seats cannot cancel a different one the
        /// organization bought separately and still owns.
        /// </remarks>
        public async Task RevokeAsync(PaymentOrder order)
        {
            if (order == null) return;

            if (!string.IsNullOrWhiteSpace(order.GrantedAddOnId))
            {
                var grant = (await _addOns.GetActiveForOrganizationAsync(order.OrganizationId))
                    .FirstOrDefault(g => g.Id == order.GrantedAddOnId);

                if (grant != null)
                {
                    grant.Status = "cancelled";
                    grant.EndDate = DateTime.UtcNow;
                    await _addOns.UpsertPurchaseAsync(grant);
                }
                else
                {
                    // Already gone - cancelled by the customer before the refund landed, say.
                    // Not an error, but worth saying so, because the alternative reading is that
                    // a refund silently failed to revoke anything.
                    _logger.LogInformation(
                        "Order {OrderId} refunded; its grant {GrantId} was already inactive.",
                        order.Id, order.GrantedAddOnId);
                }
            }

            order.Status = PaymentOrderStatus.Refunded;
            order.CompletedAt = DateTime.UtcNow;
            order.Note = string.IsNullOrWhiteSpace(order.Note)
                ? "Refunded by the payment provider."
                : order.Note + " | Refunded by the payment provider.";

            await _orders.UpdateAsync(order);

            _entitlements.Invalidate(order.OrganizationId);
        }

        private static string Combine(string baseUrl, string query)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            return baseUrl.Contains('?')
                ? baseUrl + "&" + query.TrimStart('?')
                : baseUrl.TrimEnd('/') + query;
        }
    }
}
