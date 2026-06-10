using System.Text;
using System.Text.Json;

namespace SqlSpider;

// the one error type the CLI/engine throw for user-facing failures (bad args, missing files).
public sealed class CliError : Exception { public CliError(string m) : base(m) { } }

// ---------------------------------------------------------------------------
// viz: render an extracted graph.json to ONE self-contained interactive HTML.
// ---------------------------------------------------------------------------
// Reads the graphify node-link json the tool emits (top-level `nodes` + `links`; each node has
// id/label/metadata.kind, each link has source/target/relation) and writes a single HTML file
// that draws a force-directed, draggable graph via vis-network from a CDN. The graph data is
// embedded inline -- no server, no separate data file, no build step. Open it in any browser.
public static class Viz
{
    // distinct, legible palette keyed by metadata.kind. anything unknown falls back to grey.
    static readonly Dictionary<string, string> Palette = new()
    {
        ["table"]    = "#4e79a7",   // blue
        ["view"]     = "#59a14f",   // green
        ["proc"]     = "#e15759",   // red
        ["function"] = "#f28e2b",   // orange
        ["trigger"]  = "#b07aa1",   // purple
        ["column"]   = "#9c755f",   // brown
        ["script"]   = "#bab0ac",   // grey
    };
    const string Fallback = "#888888";

    public static int Run(string[] args)
    {
        if (args.Length < 1) throw new CliError("usage: viz <graph.json> [out.html]");
        var graphPath = args[0];
        var outPath   = args.Length > 1 ? args[1] : Path.ChangeExtension(graphPath, ".html");
        if (!File.Exists(graphPath)) throw new CliError($"graph file not found: {graphPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(graphPath));
        var root = doc.RootElement;

        // ---- links first: source, target, relation -> vis-network edge {from,to,title,label} ----
        // parsed before the nodes so each node's degree (its edge count, in + out) is known when the
        // node is emitted; degree drives node size so the hubs read as hubs instead of uniform dots.
        var visEdges = new List<Dictionary<string, object>>();
        var degree = new Dictionary<string, int>();
        if (root.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in links.EnumerateArray())
            {
                var from = l.TryGetProperty("source", out var ps) ? ps.GetString() ?? "" : "";
                var to   = l.TryGetProperty("target", out var pt) ? pt.GetString() ?? "" : "";
                if (from.Length == 0 || to.Length == 0) continue;
                var rel  = l.TryGetProperty("relation", out var pr) ? pr.GetString() ?? "" : "";
                degree[from] = degree.GetValueOrDefault(from) + 1;
                degree[to]   = degree.GetValueOrDefault(to) + 1;
                visEdges.Add(new()
                {
                    ["from"] = from, ["to"] = to,
                    ["title"] = rel,        // shown on hover
                    ["label"] = rel,        // shown on the edge
                    ["arrows"] = "to",
                });
            }
        }

        // ---- nodes: id, label, kind -> vis-network node {id,label,color,group,title,value} ----
        // `value` (the node's degree) feeds vis-network's scaling, sized on a log scale via
        // customScalingFunction in the template so a 600-edge hub doesn't flatten everything else.
        var visNodes = new List<Dictionary<string, object>>();
        var kindsSeen = new SortedSet<string>();
        if (root.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in nodes.EnumerateArray())
            {
                var id    = n.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
                if (id.Length == 0) continue;
                var label = n.TryGetProperty("label", out var pl) ? pl.GetString() ?? id : id;
                var kind  = "script";
                if (n.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object
                    && md.TryGetProperty("kind", out var pk) && pk.ValueKind == JsonValueKind.String)
                    kind = pk.GetString() ?? "script";
                kindsSeen.Add(kind);
                var color = Palette.TryGetValue(kind, out var c) ? c : Fallback;
                var deg   = degree.GetValueOrDefault(id);
                visNodes.Add(new()
                {
                    ["id"] = id, ["label"] = label, ["group"] = kind,
                    ["title"] = $"{label} ({kind}, {deg} connection{(deg == 1 ? "" : "s")})",
                    ["color"] = color,
                    ["value"] = deg,
                });
            }
        }

        var jsonOpts = new JsonSerializerOptions { WriteIndented = false };
        var nodesJson  = JsonSerializer.Serialize(visNodes, jsonOpts);
        var edgesJson  = JsonSerializer.Serialize(visEdges, jsonOpts);

        // legend rows for the kinds that actually appear
        var legend = new StringBuilder();
        foreach (var k in kindsSeen)
        {
            var color = Palette.TryGetValue(k, out var c) ? c : Fallback;
            legend.Append($"<span class=\"k\"><i style=\"background:{color}\"></i>{System.Net.WebUtility.HtmlEncode(k)}</span>");
        }

        var html = Template(nodesJson, edgesJson, legend.ToString(),
            visNodes.Count, visEdges.Count, Path.GetFileName(graphPath));
        File.WriteAllText(outPath, html);
        Console.WriteLine($"wrote {outPath}  ({visNodes.Count} nodes, {visEdges.Count} edges)  -- open it in any browser");
        return 0;
    }

    // one self-contained HTML file: a single CDN <script> for vis-network, the node/edge data
    // embedded inline, force layout on, nodes draggable, edge relations shown as titles/labels.
    static string Template(string nodesJson, string edgesJson, string legend, int nNodes, int nEdges, string srcName) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>sql-spider graph: {{srcName}}</title>
<script src="https://unpkg.com/vis-network/standalone/umd/vis-network.min.js"></script>
<style>
  html, body { margin: 0; height: 100%; font-family: system-ui, sans-serif; }
  #graph { width: 100%; height: 100vh; }
  #bar {
    position: fixed; top: 0; left: 0; right: 0; padding: 6px 10px;
    background: rgba(255,255,255,.92); border-bottom: 1px solid #ddd;
    font-size: 13px; color: #333; z-index: 10; display: flex; gap: 14px; align-items: center; flex-wrap: wrap;
  }
  #bar b { font-weight: 600; }
  .k { display: inline-flex; align-items: center; gap: 4px; }
  .k i { width: 11px; height: 11px; border-radius: 2px; display: inline-block; }
</style>
</head>
<body>
<div id="bar">
  <b>sql-spider</b>
  <span>{{srcName}}: {{nNodes}} nodes, {{nEdges}} edges</span>
  {{legend}}
  <span style="margin-left:auto;color:#888">drag nodes · scroll to zoom · hover an edge for its relation</span>
</div>
<div id="graph"></div>
<script>
  // graph data embedded inline (no separate file to serve)
  const nodes = new vis.DataSet({{nodesJson}});
  const edges = new vis.DataSet({{edgesJson}});
  const container = document.getElementById("graph");
  const data = { nodes, edges };
  const options = {
    nodes: {
      shape: "dot", size: 14, font: { size: 13, face: "system-ui" },
      // size by degree (the node's `value`), log-scaled: hubs like a core table with hundreds of
      // edges read big, leaf columns read small, and one god-node can't flatten the middle tiers.
      scaling: {
        min: 4, max: 56,
        label: { enabled: true, min: 9, max: 30 },
        customScalingFunction: (min, max, total, value) => {
          if (max === min) return 0.5;
          return Math.log1p(value) / Math.log1p(max);
        }
      }
    },
    edges: {
      font: { size: 10, color: "#777", align: "middle", strokeWidth: 3, strokeColor: "#fff" },
      color: { color: "#bbb", highlight: "#555", hover: "#555" },
      smooth: { type: "continuous" },
      arrows: { to: { enabled: true, scaleFactor: 0.5 } }
    },
    physics: {
      enabled: true,
      solver: "forceAtlas2Based",
      forceAtlas2Based: { gravitationalConstant: -50, springLength: 110, springConstant: 0.08 },
      stabilization: { iterations: 200 }
    },
    interaction: { hover: true, dragNodes: true, tooltipDelay: 120, navigationButtons: false }
  };
  new vis.Network(container, data, options);
</script>
</body>
</html>
""";
}
