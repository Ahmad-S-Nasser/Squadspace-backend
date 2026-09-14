using System.Text.Json;
using System.Text.Json.Serialization;
using RafeeqyNotes.Api.Models;

public class WhiteboardElementConverter : JsonConverter<WhiteboardElement>
{
    public override WhiteboardElement? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {

        using var doc = JsonDocument.ParseValue(ref reader);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp) &&
    !doc.RootElement.TryGetProperty("Type", out typeProp))
        {
            throw new JsonException("Missing Type discriminator for WhiteboardElement.");
        }

        var type = typeProp.GetString();
        return type switch
        {
            "sticky" => JsonSerializer.Deserialize<StickyNoteElement>(doc.RootElement.GetRawText(), options),
            "shape" => JsonSerializer.Deserialize<ShapeElement>(doc.RootElement.GetRawText(), options),
            "text" => JsonSerializer.Deserialize<TextElement>(doc.RootElement.GetRawText(), options),
            "image" => JsonSerializer.Deserialize<ImageElement>(doc.RootElement.GetRawText(), options),
            "connection" => JsonSerializer.Deserialize<ConnectionElement>(doc.RootElement.GetRawText(), options),
            _ => throw new JsonException($"Unknown WhiteboardElement type: {type}")
        };
    }

    public override void Write(Utf8JsonWriter writer, WhiteboardElement value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case StickyNoteElement sticky:
                JsonSerializer.Serialize(writer, sticky, options);
                break;
            case ShapeElement shape:
                JsonSerializer.Serialize(writer, shape, options);
                break;
            case TextElement text:
                JsonSerializer.Serialize(writer, text, options);
                break;
            case ImageElement image:
                JsonSerializer.Serialize(writer, image, options);
                break;
            case ConnectionElement conn:
                JsonSerializer.Serialize(writer, conn, options);
                break;
            default:
                throw new JsonException($"Unknown WhiteboardElement type: {value.GetType().Name}");
        }
    }
}