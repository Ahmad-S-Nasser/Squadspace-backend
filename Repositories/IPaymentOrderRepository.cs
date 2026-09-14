using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IPaymentOrderRepository
    {
        Task CreateAsync(PaymentOrder order);

        Task<PaymentOrder> GetByIdAsync(string id);

        /// <summary>Orders for one organization, newest first.</summary>
        Task<List<PaymentOrder>> GetForOrganizationAsync(string organizationId, int limit = 100);

        /// <summary>Orders in any of the given statuses, newest first. For the triage screen.</summary>
        Task<List<PaymentOrder>> GetByStatusAsync(string[] statuses, int limit = 200);

        /// <summary>
        /// Moves an order to a terminal state, but only from an open one.
        /// </summary>
        /// <remarks>
        /// Returns false when the order was already closed, and that return value is the whole
        /// point: it is a conditional update, so two concurrent webhooks for the same payment
        /// cannot both win. Whichever arrives second is told the order was already settled and
        /// provisions nothing, which is what stops a retry from granting the add-on twice.
        /// </remarks>
        Task<bool> TryCloseAsync(
            string id, string status, string providerReference, string grantedAddOnId, string note);

        Task UpdateAsync(PaymentOrder order);
    }
}
