using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>Where an order is in its life.</summary>
    /// <remarks>
    /// Two routes reach <see cref="Paid"/>. A card payment goes
    /// <see cref="PendingPayment"/> → <see cref="Paid"/> on a verified webhook. With no gateway
    /// configured it goes <see cref="PendingApproval"/> → <see cref="Paid"/> when a platform
    /// admin approves it. Both end in the same place and provision through the same code, so the
    /// manual route is not a parallel implementation that can drift from the paid one.
    /// </remarks>
    public static class PaymentOrderStatus
    {
        /// <summary>Checkout started. Nothing is granted yet.</summary>
        public const string PendingPayment = "pending_payment";

        /// <summary>No gateway configured; a human has to approve it.</summary>
        public const string PendingApproval = "pending_approval";

        /// <summary>Settled and provisioned. Terminal.</summary>
        public const string Paid = "paid";

        /// <summary>The gateway declined it. Terminal.</summary>
        public const string Failed = "failed";

        /// <summary>Withdrawn by the buyer, or an approval request turned down. Terminal.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>Money returned. Whatever was granted has been revoked.</summary>
        public const string Refunded = "refunded";

        public static readonly string[] All =
        {
            PendingPayment, PendingApproval, Paid, Failed, Cancelled, Refunded,
        };

        /// <summary>Statuses that can still change. Anything else is done with.</summary>
        public static readonly string[] Open = { PendingPayment, PendingApproval };
    }

    /// <summary>
    /// One attempt to buy something, and the audit trail of what happened to it.
    /// </summary>
    /// <remarks>
    /// This record exists so that money and entitlement are never the same fact. The add-on grant
    /// (<see cref="OrganizationAddOn"/>) says what an organization currently has; this says what
    /// was bought, by whom, at what price, through which provider, and whether it was ever paid
    /// for. Collapsing the two would mean either an entitlement row you cannot reconcile against
    /// a bank statement, or a payment history that changes when someone cancels a seat.
    ///
    /// <see cref="Id"/> is deliberately the same string sent to the gateway as its order
    /// reference. That is what makes the webhook a lookup rather than a search, and it is what
    /// makes replays idempotent: a second webhook for the same payment finds the same document
    /// and sees it is already <see cref="PaymentOrderStatus.Paid"/>.
    ///
    /// Prices are stored ON the order rather than read from the catalogue at fulfilment time. A
    /// catalogue price can change between checkout and webhook, and the customer must be charged
    /// and granted what they were shown.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class PaymentOrder
    {
        /// <summary>Also the gateway's order reference. Unique.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string OrganizationId { get; set; }

        /// <summary>Always taken from the JWT, never from the request body.</summary>
        public string RequestedByUserId { get; set; }

        public string RequestedByName { get; set; }
        public string RequestedByEmail { get; set; }

        public string AddOnId { get; set; }

        /// <summary>Snapshot of the name at purchase time, so an audit row survives a rename.</summary>
        public string AddOnName { get; set; }

        public int Quantity { get; set; } = 1;

        /// <summary>"monthly" or "yearly".</summary>
        public string BillingCycle { get; set; } = "monthly";

        /// <summary>Catalogue price for one unit, at the moment of purchase.</summary>
        public decimal UnitPrice { get; set; }

        /// <summary>UnitPrice x Quantity. What the gateway was asked to charge.</summary>
        public decimal Amount { get; set; }

        /// <summary>ISO 4217, uppercase.</summary>
        public string Currency { get; set; }

        /// <summary>A value from <see cref="PaymentOrderStatus"/>.</summary>
        public string Status { get; set; } = PaymentOrderStatus.PendingPayment;

        /// <summary>Gateway key ("kashier"), or "manual" when approved by hand.</summary>
        public string Provider { get; set; }

        /// <summary>The gateway's transaction id, for reconciliation and support.</summary>
        public string ProviderReference { get; set; }

        /// <summary>
        /// The <see cref="OrganizationAddOn"/> this order created.
        /// </summary>
        /// <remarks>
        /// The second half of idempotent provisioning: set in the same write that marks the order
        /// paid, so a replayed webhook can tell "already granted" from "not granted yet" without
        /// trusting the status alone, and cancelling the order can revoke exactly what it granted.
        /// </remarks>
        public string GrantedAddOnId { get; set; }

        /// <summary>Why it was declined, or an internal note from whoever approved it.</summary>
        public string Note { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>When it reached a terminal status.</summary>
        public DateTime? CompletedAt { get; set; }
    }
}
