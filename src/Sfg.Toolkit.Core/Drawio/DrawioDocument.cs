using System.Xml.Linq;

namespace Sfg.Toolkit.Core.Drawio;

/// <summary>
/// Minimal deterministic drawio document builder following the book's diagram
/// conventions: mxfile agent="vibe", rounded nodes, orthogonal edges, house palette.
/// </summary>
public sealed class DrawioDocument
{
    private readonly List<Node> _nodes = new();
    private readonly List<Edge> _edges = new();

    public void AddNode(string id, string label, string style, double x, double y, double width, double height)
    {
        _nodes.Add(new Node(id, label, style, x, y, width, height));
    }

    public void AddEdge(string id, string source, string target, string label, string style)
    {
        _edges.Add(new Edge(id, source, target, label, style));
    }

    public string ToXml(int pageWidth, int pageHeight, string diagramName = "ContextMap", string diagramId = "context-map")
    {
        var mxfile = new XElement("mxfile", new XAttribute("agent", "vibe"));
        var diagram = new XElement("diagram", new XAttribute("id", diagramId), new XAttribute("name", diagramName));
        var model = new XElement(
            "mxGraphModel",
            new XAttribute("dx", "800"),
            new XAttribute("dy", "600"),
            new XAttribute("grid", "1"),
            new XAttribute("gridSize", "10"),
            new XAttribute("page", "1"),
            new XAttribute("pageWidth", pageWidth),
            new XAttribute("pageHeight", pageHeight));
        var root = new XElement("root");
        root.Add(new XElement("mxCell", new XAttribute("id", "0")));
        root.Add(new XElement("mxCell", new XAttribute("id", "1"), new XAttribute("parent", "0")));

        foreach (var node in _nodes)
        {
            var cell = new XElement(
                "mxCell",
                new XAttribute("id", node.Id),
                new XAttribute("value", node.Label),
                new XAttribute("style", node.Style),
                new XAttribute("vertex", "1"),
                new XAttribute("parent", "1"));
            cell.Add(new XElement(
                "mxGeometry",
                new XAttribute("x", node.X),
                new XAttribute("y", node.Y),
                new XAttribute("width", node.Width),
                new XAttribute("height", node.Height),
                new XAttribute("as", "geometry")));
            root.Add(cell);
        }

        foreach (var edge in _edges)
        {
            var cell = new XElement(
                "mxCell",
                new XAttribute("id", edge.Id),
                new XAttribute("value", edge.Label),
                new XAttribute("style", edge.Style),
                new XAttribute("edge", "1"),
                new XAttribute("parent", "1"),
                new XAttribute("source", edge.Source),
                new XAttribute("target", edge.Target));
            cell.Add(new XElement("mxGeometry", new XAttribute("relative", "1"), new XAttribute("as", "geometry")));
            root.Add(cell);
        }

        model.Add(root);
        diagram.Add(model);
        mxfile.Add(diagram);
        return mxfile.ToString();
    }

    private sealed record Node(string Id, string Label, string Style, double X, double Y, double Width, double Height);

    private sealed record Edge(string Id, string Source, string Target, string Label, string Style);
}
