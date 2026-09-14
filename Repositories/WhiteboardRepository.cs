using MongoDB.Bson;
using MongoDB.Driver;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Config;

namespace RafeeqyNotes.Api.Repositories
{
    public class WhiteboardRepository : IWhiteboardRepository
    {
        private readonly IMongoCollection<Whiteboard> _boards;

        public WhiteboardRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _boards = database.GetCollection<Whiteboard>("Whiteboards");
        }

        public async Task<List<Whiteboard>> GetByProjectIdAsync(string projectId) =>
            await _boards.Find(b => b.ProjectId == projectId).ToListAsync();

        public async Task<Whiteboard?> GetByIdAsync(string id) =>
            await _boards.Find(b => b.Id == id).FirstOrDefaultAsync();

        public async Task CreateAsync(Whiteboard board) =>
            await _boards.InsertOneAsync(board);

        public async Task UpdateAsync(Whiteboard board) =>
            await _boards.ReplaceOneAsync(b => b.Id == board.Id, board);

        public async Task<bool> DeleteAsync(string id)
        {
            var result = await _boards.DeleteOneAsync(b => b.Id == id);
            return result.DeletedCount > 0;
        }

        public async Task AddElementAsync(string boardId, WhiteboardElement element)
        {
            var filter = Builders<Whiteboard>.Filter.Eq(b => b.Id, boardId);
            var update = Builders<Whiteboard>.Update
                .Push(b => b.Elements, element)
                .Set(b => b.UpdatedAt, DateTime.UtcNow);
            await _boards.UpdateOneAsync(filter, update);
        }

        public async Task UpdateElementAsync(string boardId, string elementId, WhiteboardElement element)
        {
            var filter = Builders<Whiteboard>.Filter.Eq(b => b.Id, boardId);
            var update = Builders<Whiteboard>.Update
                .Set<WhiteboardElement>("Elements.$[elem]", element)
                .Set(b => b.UpdatedAt, DateTime.UtcNow);
            var arrayFilters = new List<ArrayFilterDefinition>
            {
                new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument("elem._id", elementId))
            };
            await _boards.UpdateOneAsync(filter, update, new UpdateOptions { ArrayFilters = arrayFilters });
        }

        public async Task<bool> RemoveElementAsync(string boardId, string elementId)
        {
            var filter = Builders<Whiteboard>.Filter.Eq(b => b.Id, boardId);
            var update = Builders<Whiteboard>.Update
                .PullFilter(b => b.Elements, Builders<WhiteboardElement>.Filter.Eq(e => e.Id, elementId))
                .Set(b => b.UpdatedAt, DateTime.UtcNow);
            var result = await _boards.UpdateOneAsync(filter, update);
            return result.ModifiedCount > 0;
        }
    }
}