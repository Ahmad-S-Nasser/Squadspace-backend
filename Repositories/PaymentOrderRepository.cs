using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    /// <inheritdoc cref="IPaymentOrderRepository"/>
    public class PaymentOrderRepository : IPaymentOrderRepository
    {
        private readonly IMongoCollection<PaymentOrder> _orders;

        public PaymentOrderRepository(MongoDbSettings settings)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _orders = database.GetCollection<PaymentOrder>("PaymentOrders");
        }

        public async Task CreateAsync(PaymentOrder order) =>
            await _orders.InsertOneAsync(order);

        public async Task<PaymentOrder> GetByIdAsync(string id) =>
            string.IsNullOrWhiteSpace(id)
                ? null
                : await _orders.Find(o => o.Id == id).FirstOrDefaultAsync();

        public async Task<List<PaymentOrder>> GetForOrganizationAsync(string organizationId, int limit = 100)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return new List<PaymentOrder>();

            return await _orders
                .Find(o => o.OrganizationId == organizationId)
                .SortByDescending(o => o.CreatedAt)
                .Limit(Math.Clamp(limit, 1, 500))
                .ToListAsync();
        }

        public async Task<List<PaymentOrder>> GetByStatusAsync(string[] statuses, int limit = 200)
        {
            var filter = statuses == null || statuses.Length == 0
                ? Builders<PaymentOrder>.Filter.Empty
                : Builders<PaymentOrder>.Filter.In(o => o.Status, statuses);

            return await _orders
                .Find(filter)
                .SortByDescending(o => o.CreatedAt)
                .Limit(Math.Clamp(limit, 1, 1000))
                .ToListAsync();
        }

        /// <remarks>
        /// The filter carries the status precondition, so the database decides the winner rather
        /// than the application: two webhook deliveries racing each other both issue this update,
        /// and exactly one matches an open order. Doing it as read-then-write in C# would leave a
        /// window between the two in which both callers see "open" and both provision.
        /// </remarks>
        public async Task<bool> TryCloseAsync(
            string id, string status, string providerReference, string grantedAddOnId, string note)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            var b = Builders<PaymentOrder>.Filter;
            var filter = b.And(
                b.Eq(o => o.Id, id),
                b.In(o => o.Status, PaymentOrderStatus.Open));

            var update = Builders<PaymentOrder>.Update
                .Set(o => o.Status, status)
                .Set(o => o.UpdatedAt, DateTime.UtcNow)
                .Set(o => o.CompletedAt, DateTime.UtcNow);

            // Only written when supplied: a failure notification carrying no transaction id must
            // not erase the reference an earlier attempt recorded.
            if (!string.IsNullOrWhiteSpace(providerReference))
            {
                update = update.Set(o => o.ProviderReference, providerReference);
            }

            if (!string.IsNullOrWhiteSpace(grantedAddOnId))
            {
                update = update.Set(o => o.GrantedAddOnId, grantedAddOnId);
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                update = update.Set(o => o.Note, note);
            }

            var result = await _orders.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }

        public async Task UpdateAsync(PaymentOrder order)
        {
            order.UpdatedAt = DateTime.UtcNow;
            await _orders.ReplaceOneAsync(o => o.Id == order.Id, order);
        }
    }
}
