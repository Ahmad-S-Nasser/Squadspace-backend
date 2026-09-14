
namespace RafeeqyNotes.Api.Models
{
    public class Position
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    public class Size
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }

    // Base element - use as discriminated union
    public abstract class WhiteboardElement
    {
        public string Id { get; set; }
        public string Type { get; set; }  // "sticky" | "shape" | "text" | "connection"
        public Position Position { get; set; }
        public int ZIndex { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public double? Opacity { get; set; } // 0-1, defaults to 1 when unset
    }

    public class StickyNoteElement : WhiteboardElement
    {
        public string Content { get; set; }
        public string Color { get; set; }  // "yellow" | "pink" | "blue" | "green" | "purple" | "orange"
        public Size Size { get; set; }
        public LinkedItem? LinkedItem { get; set; }
    }

    public class ShapeElement : WhiteboardElement
    {
        public string ShapeType { get; set; }  // "rectangle" | "circle" | "diamond" | "arrow"
        public Size Size { get; set; }
        public string Fill { get; set; }
        public string Stroke { get; set; }
        public int StrokeWidth { get; set; }
        public string? Text { get; set; }
        public string? FromElementId { get; set; }
        public string? ToElementId { get; set; }
        public string? FromPort { get; set; } // "top" | "bottom" | "left" | "right" | "top-left" | "top-right" | "bottom-left" | "bottom-right"
        public string? ToPort { get; set; }   // "top" | "bottom" | "left" | "right" | "top-left" | "top-right" | "bottom-left" | "bottom-right"
        public Position? StartPoint { get; set; }
        public Position? EndPoint { get; set; }
        public string? StrokeType { get; set; } // "smooth" | "straight" | "step"
    }

    public class ImageElement : WhiteboardElement
    {
        public string AttachmentId { get; set; }
        public Size Size { get; set; }
    }

    public class TextElement : WhiteboardElement
    {
        public string Content { get; set; }
        public Size Size { get; set; }
        public int FontSize { get; set; }
        public string FontWeight { get; set; }  // "normal" | "bold"
        public string Color { get; set; }
    }

    public class ConnectionElement : WhiteboardElement
    {
        public string FromElementId { get; set; }
        public string ToElementId { get; set; }
        public string? FromPort { get; set; }
        public string? ToPort { get; set; }
        public string StrokeColor { get; set; }
        public int StrokeWidth { get; set; }
        public bool ArrowHead { get; set; } = false;
        public Position? StartPoint { get; set; }     // {x, y} for free start point
        public Position? EndPoint { get; set; }    // {x, y} for free end point
        public string? StrokeType { get; set; }
    }

}
