using MongoDB.Bson.Serialization;
using RafeeqyNotes.Api.Models;

public static class MongoMappings
{
    public static void RegisterClassMaps()
    {
        if (!BsonClassMap.IsClassMapRegistered(typeof(WhiteboardElement)))
        {
            BsonClassMap.RegisterClassMap<WhiteboardElement>(cm =>
            {
                cm.AutoMap();
                cm.SetIsRootClass(true);
                cm.MapMember(c => c.Type);
            });

            BsonClassMap.RegisterClassMap<StickyNoteElement>(cm => cm.AutoMap());
            BsonClassMap.RegisterClassMap<ShapeElement>(cm => cm.AutoMap());
            BsonClassMap.RegisterClassMap<TextElement>(cm => cm.AutoMap());
            BsonClassMap.RegisterClassMap<ImageElement>(cm => cm.AutoMap());
            BsonClassMap.RegisterClassMap<ConnectionElement>(cm => cm.AutoMap());
        }
    }
}