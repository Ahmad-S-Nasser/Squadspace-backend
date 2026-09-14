using Hxl.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Where the payment provider talks to us.
    /// </summary>
    /// <remarks>
    /// ANONYMOUS BY NECESSITY, AUTHENTICATED BY SIGNATURE.
    ///
    /// A gateway cannot present a bearer token, so this controller carries no [Authorize] and no
    /// organization guard. Its guard is the HMAC signature on the payload, verified by the
    /// payment library before this code reads a single field. That is why every path below
    /// begins with IsAuthentic and why nothing - not a log line describing the order, not a
    /// status change, not a 404 that would confirm an id exists - happens before it passes.
    ///
    /// This is the only endpoint in the system that grants paid entitlement to an unauthenticated
    /// caller. The rules it follows:
    ///
    ///   1. Verify the signature over the RAW body, before parsing.
    ///   2. Confirm the amount and currency match what we stored. A signature proves the message
    ///      is genuine, not that it is for the order we think it is.
    ///   3. Fulfil idempotently. Gateways retry, and a retry must not grant twice.
    ///   4. Answer 200 to anything authentic, including a repeat. A non-2xx makes the gateway
    ///      retry forever over something we have already handled.
    /// </remarks>
    [Route("api/payments")]
    [ApiController]
    [AllowAnonymous]
    public class PaymentsController : ControllerBase
    {
        private readonly IPaymentGateway _gateway;
        private readonly IPaymentOrderRepository _orders;
        private readonly IAddOnPurchaseService _purchases;
        private readonly ILogger<PaymentsController> _logger;

        public PaymentsController(
            IPaymentGateway gateway,
            IPaymentOrderRepository orders,
            IAddOnPurchaseService purchases,
            ILogger<PaymentsController> logger)
        {
            _gateway = gateway;
            _orders = orders;
            _purchases = purchases;
            _logger = logger;
        }

        /// <summary>The server-to-server notification. This is what actually grants the add-on.</summary>
        [HttpPost("webhook/{provider}")]
        public async Task<IActionResult> Webhook(string provider)
        {
            // Read the body EXACTLY as sent. Model binding would parse and re-render it, and the
            // signature is over the bytes on the wire, not over an equivalent JSON document.
            string rawBody;
            using (var reader = new StreamReader(Request.Body))
            {
                rawBody = await reader.ReadToEndAsync();
            }

            var headers = Request.Headers.ToDictionary(
                h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            var notification = _gateway.ReadNotification(headers, rawBody);

            if (!notification.IsAuthentic)
            {
                // Deliberately vague to the caller and specific in the log. Telling an
                // unauthenticated caller WHY their forgery failed is free debugging for them.
                _logger.LogWarning(
                    "Rejected a {Provider} webhook: {Reason}", provider, notification.Rejection);

                return BadRequest(new { message = "Invalid signature." });
            }

            if (string.IsNullOrWhiteSpace(notification.OrderId))
            {
                _logger.LogWarning("A verified {Provider} webhook carried no order id.", provider);
                return Ok(new { handled = false });
            }

            var order = await _orders.GetByIdAsync(notification.OrderId);
            if (order == null)
            {
                // Verified, but about an order we do not have. Worth a warning rather than a
                // shrug: it means an order was never written, or the account is shared with
                // another system that is now sending us its traffic.
                _logger.LogWarning(
                    "Verified {Provider} webhook for unknown order {OrderId}.", provider, notification.OrderId);

                return Ok(new { handled = false });
            }

            switch (notification.Status)
            {
                case PaymentStatus.Paid:
                    if (!AmountMatches(order, notification))
                    {
                        // Authentic message, wrong money. Never fulfil, and never quietly fail:
                        // this is either a misconfiguration or someone paying a different amount
                        // than the one they were quoted, and both need a human.
                        _logger.LogError(
                            "Order {OrderId} was paid as {PaidAmount} {PaidCurrency} but was raised for " +
                            "{OrderAmount} {OrderCurrency}. NOT fulfilled.",
                            order.Id, notification.Amount, notification.Currency, order.Amount, order.Currency);

                        return Ok(new { handled = false, reason = "amount_mismatch" });
                    }

                    var granted = await _purchases.FulfilAsync(
                        order, provider, notification.ProviderReference, null);

                    return Ok(new { handled = true, granted });

                case PaymentStatus.Failed:
                    await _purchases.CloseUnfulfilledAsync(
                        order, PaymentOrderStatus.Failed, "The payment provider reported a failure.");

                    return Ok(new { handled = true, granted = false });

                case PaymentStatus.Refunded:
                    await RevokeAsync(order);
                    return Ok(new { handled = true, revoked = true });

                case PaymentStatus.Pending:
                    // Accepted but not settled. Leave the order open and grant nothing; the
                    // provider will send another notification when it clears.
                    _logger.LogInformation("Order {OrderId} is pending settlement.", order.Id);
                    return Ok(new { handled = true, granted = false });

                default:
                    _logger.LogWarning(
                        "Order {OrderId} received an unrecognised status; granting nothing.", order.Id);
                    return Ok(new { handled = false });
            }
        }

        /// <summary>
        /// Where the payer's browser lands. Shows a result; grants nothing.
        /// </summary>
        /// <remarks>
        /// Read-only on purpose. This request comes from the payer's browser, so acting on it
        /// would mean granting entitlement on the say-so of whoever can type a URL. It also
        /// simply does not arrive when the payer closes the tab, which is why the webhook is the
        /// side that fulfils. The status returned here is the ORDER's own status, not the
        /// redirect's claim about it - so a forged redirect shows the truth.
        /// </remarks>
        [HttpGet("return")]
        public async Task<IActionResult> Return([FromQuery] string order)
        {
            if (string.IsNullOrWhiteSpace(order)) return BadRequest(new { message = "No order was named." });

            var stored = await _orders.GetByIdAsync(order);
            if (stored == null) return NotFound(new { message = "No such order." });

            return Ok(new
            {
                orderId = stored.Id,
                status = stored.Status,
                addOnId = stored.AddOnId,
                addOnName = stored.AddOnName,
                quantity = stored.Quantity,
                amount = stored.Amount,
                currency = stored.Currency,

                // The single fact the page needs, computed here rather than left to the client
                // to infer from a status string it might get wrong.
                settled = stored.Status == PaymentOrderStatus.Paid,
            });
        }

        /// <summary>
        /// Compares what was charged against what was raised.
        /// </summary>
        /// <remarks>
        /// A notification with no amount at all is accepted: not every provider echoes it, and
        /// refusing would mean refusing every legitimate payment on such a provider. When an
        /// amount IS present it must match exactly - and the currency with it, since 300 EGP and
        /// 300 USD are the same number and very different money.
        /// </remarks>
        private static bool AmountMatches(PaymentOrder order, PaymentNotification notification)
        {
            if (notification.Amount is null) return true;
            if (notification.Amount.Value != order.Amount) return false;

            if (string.IsNullOrWhiteSpace(notification.Currency)) return true;

            return string.Equals(notification.Currency, order.Currency, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Takes back what a refunded order granted.</summary>
        private async Task RevokeAsync(PaymentOrder order)
        {
            _logger.LogWarning(
                "Order {OrderId} was refunded; revoking add-on {AddOnId} from org {OrgId}.",
                order.Id, order.AddOnId, order.OrganizationId);

            await _purchases.RevokeAsync(order);
        }
    }
}
