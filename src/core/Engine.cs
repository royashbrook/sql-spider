using System.Text.Json;

namespace SqlSpider;

// ---------------------------------------------------------------------------
// extract: parse the corpus into a graphify node-link graph + frontier + audits
// ---------------------------------------------------------------------------
// This is the DIALECT-NEUTRAL core. It picks an IDialectExtractor (T-SQL by default,
// or another dialect via --dialect), hands it the corpus, and gets back dependency
// FACTS. Everything below -- node merge, edge dedup, the frontier, join keys, the
// connected-components audit, lineage roll-up, the console report -- works on those
// facts and never touches SQL text, so it is identical across every dialect.
public static class Engine
{
    public static int RunExtract(string[] args, System.Func<string, IDialectExtractor> pick)
    {
        // --dialect picks the parser; default tsql so existing behavior is untouched.
        // --graphify [out.json] ALSO writes a graphify-spec graph.json (edge relations mapped onto
        //   graphify's fixed vocabulary). OPT-IN: the native graph.json is always written and is
        //   unchanged. Default graphify path is <corpus>/graphify-out/graph.json (graphify's own
        //   convention), so `graphify cluster-only <corpus>` / `/graphify` just works after.
        string dialect = "tsql";
        bool graphify = false;
        bool graphifyStandard = false;   // collapse to graphify's blessed enum (default: keep our richer vocab)
        string? graphifyOut = null;
        var pos = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--dialect" && i + 1 < args.Length) dialect = args[++i].Trim().ToLowerInvariant();
            // value only via --graphify=path: a bare flag that consumed the NEXT token used to
            // swallow positionals (extract corpus --graphify graph.json frontier.json silently
            // destroyed the native graph). the = form is unambiguous.
            else if (args[i] == "--graphify") graphify = true;
            else if (args[i].StartsWith("--graphify=")) { graphify = true; graphifyOut = args[i]["--graphify=".Length..]; }
            else if (args[i] == "--graphify-standard") { graphify = true; graphifyStandard = true; }
            else pos.Add(args[i]);
        }

        var dir      = pos.Count > 0 ? pos[0] : "./corpus";
        var outPath  = pos.Count > 1 ? pos[1] : Path.Combine(dir, "graph.json");
        var frontOut = pos.Count > 2 ? pos[2] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "frontier.json");

        if (!Directory.Exists(dir)) throw new CliError($"corpus dir not found: {dir}");

        var files = Directory.GetFiles(dir, "*.sql").OrderBy(x => x).ToArray();
        // an empty corpus used to be vacuously "closed" (0 components <= 1) -- a typo'd dir or a
        // forgotten absorb would pass the CI gate. fail loudly instead.
        if (files.Length == 0) throw new CliError($"no .sql files in corpus dir: {dir}");

        // ---- DIALECT SEAM: SQL text -> dependency facts. the only dialect-specific step. ----
        var extractor = pick(dialect);
        var facts = extractor.Extract(files);

        // ---- everything below is dialect-NEUTRAL: facts -> graph + frontier + audits ----
        var nodes      = new Dictionary<string, (string label, string kind, string src)>();
        var edges      = new List<(string from, string to, string rel, string src)>();
        var defined    = new Dictionary<string, string>();              // name -> kind (objects we HAVE the definition for)
        var colCount   = new Dictionary<string, int>();                 // table.col -> usage count
        var colTables  = new Dictionary<string, HashSet<string>>();     // col -> set of tables using it
        var lineage    = new List<(string srcTbl, string srcCol, string tgtTbl, string tgtCol)>(); // col-to-col flow (update set)
        int parseFail  = facts.ParseFail;                              // files still erroring after the dialect's own fallback
        var realTables = facts.RealTables;                             // tables we have create-table DDL for (dialect-resolved)
        var cteNames   = facts.CteNames;                              // named CTEs = legit subquery sources

        // pseudo-tables / temp noise to drop (trigger inserted/deleted, #temp, @tablevars, system procs)
        static bool Noise(string t) => t == "inserted" || t == "deleted" || t == "sp_executesql"
            || t.StartsWith("#") || t.StartsWith("@") || t.StartsWith("xp_");

        void AddNode(string id, string label, string kind, string src)
        {
            if (Noise(id)) return;
            if (!nodes.ContainsKey(id)) nodes[id] = (label, kind, src);
            else if (nodes[id].kind == "table" && kind != "table" && kind != "script") nodes[id] = (label, kind, src);
        }

        foreach (var ff in facts.Files)
        {
            var fname = ff.File;

            // declared FOREIGN KEY edges (owner -> referenced table); keeps FK-only lookup tables connected.
            foreach (var (from, to) in ff.FkEdges)
            {
                if (Noise(from) || Noise(to)) continue;
                AddNode(from, from, "table", fname); AddNode(to, to, "table", fname);
                edges.Add((from, to, "fk", fname));
            }

            foreach (var c in ff.Containers)
            {
                string container = c.Name;
                // a container we hold the definition for is a real node + a frontier "defined" entry;
                // a fallback bare-script / index-only table reference is not "defined".
                if (c.Defined) defined[container] = c.Kind;
                AddNode(container, container, c.Kind, fname);
                // the DEFINING file is authoritative for a node's source_file and kind -- without
                // this, whichever file happens to sort first and merely REFERENCE the object claims
                // its source (a table's "source" showed as some random proc instead of its own DDL).
                if (c.Defined && !Noise(container)) nodes[container] = (container, c.Kind, fname);

                foreach (var t in c.Reads)  { if (Noise(t)) continue; AddNode(t, t, "table", fname);    edges.Add((container, t, "references", fname)); }
                foreach (var t in c.Writes) { if (Noise(t)) continue; AddNode(t, t, "table", fname);    edges.Add((container, t, "writes", fname)); }
                foreach (var p in c.Calls)  { if (Noise(p)) continue; AddNode(p, p, "proc", fname);     edges.Add((container, p, "calls", fname)); }
                foreach (var fn in c.Funcs) { if (Noise(fn)) continue; AddNode(fn, fn, "function", fname); edges.Add((container, fn, "calls", fname)); }
            }

            // column usages -> weight + join-key index (real-table-scoped by the dialect already)
            foreach (var (tbl, col) in ff.ColumnHits)
            {
                if (Noise(tbl)) continue;
                var key = tbl + "." + col;
                colCount[key] = colCount.GetValueOrDefault(key) + 1;
                (colTables.TryGetValue(col, out var s) ? s : colTables[col] = new HashSet<string>()).Add(tbl);
            }

            lineage.AddRange(ff.Lineage);
        }

        var edgeSet = edges.GroupBy(e => (e.from, e.to, e.rel)).Select(g => g.First()).ToList();

        // ---- FRONTIER: referenced but not defined in this corpus = the spider's next pull ----
        // functions ride with procs: both live in sys.sql_modules, so the same module-def pull
        // covers them. without this an undefined UDF passed the closure audit silently.
        var frontierProcs  = nodes.Where(n => (n.Value.kind == "proc" || n.Value.kind == "function") && !defined.ContainsKey(n.Key)).Select(n => n.Key).OrderBy(x => x).ToList();
        var frontierTables = nodes.Where(n => n.Value.kind == "table" && !defined.ContainsKey(n.Key)).Select(n => n.Key).OrderBy(x => x).ToList();

        // ---- relationship-bearing column layer: only columns shared across 3+ real tables (join keys) ----
        var joinKeys = colTables.Where(k => k.Value.Count >= 3).ToDictionary(k => k.Key, k => k.Value);

        // ---- build graphify node-link json ----
        var jnodes = new List<Dictionary<string, object>>();
        foreach (var kv in nodes)
        {
            jnodes.Add(new()
            {
                ["id"] = kv.Key, ["label"] = kv.Value.label, ["file_type"] = "code", ["source_file"] = kv.Value.src,
                ["metadata"] = new Dictionary<string, object> { ["language"] = "sql", ["kind"] = kv.Value.kind, ["defined"] = defined.ContainsKey(kv.Key) },
                ["_origin"] = "ast"
            });
        }
        foreach (var col in joinKeys.Keys)
        {
            jnodes.Add(new()
            {
                ["id"] = "col:" + col, ["label"] = col, ["file_type"] = "code", ["source_file"] = "(column)",
                ["metadata"] = new Dictionary<string, object> { ["language"] = "sql", ["kind"] = "column", ["tables"] = joinKeys[col].Count },
                ["_origin"] = "ast"
            });
        }
        var jlinks = new List<Dictionary<string, object>>();
        foreach (var e in edgeSet)
            jlinks.Add(new() { ["source"] = e.from, ["target"] = e.to, ["relation"] = e.rel, ["source_file"] = e.src, ["confidence"] = "EXTRACTED", ["weight"] = 1.0 });
        foreach (var col in joinKeys.Keys)
            foreach (var tbl in joinKeys[col])
                jlinks.Add(new() { ["source"] = tbl, ["target"] = "col:" + col, ["relation"] = "join_key", ["source_file"] = "(column)", ["confidence"] = "EXTRACTED", ["weight"] = (double)colCount.GetValueOrDefault(tbl + "." + col, 1) });

        var graph = new Dictionary<string, object>
        {
            ["directed"] = true, ["multigraph"] = true, ["graph"] = new Dictionary<string, object>(),
            ["nodes"] = jnodes, ["links"] = jlinks
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(graph, new JsonSerializerOptions { WriteIndented = false }));

        // ---- optional graphify projection: the SAME graph, relations mapped to graphify's vocab ----
        string? graphifyPath = null;
        if (graphify)
        {
            graphifyPath = graphifyOut ?? Path.Combine(dir, "graphify-out", "graph.json");
            WriteGraphify(graphifyPath, jnodes, jlinks, graphifyStandard);
        }

        // ---- connected-components audit (the success condition: ZERO orphans, ONE component) ----
        // build an undirected adjacency over every node that appears in the graph (objects + columns)
        // and walk it. More than one component, or any degree-0 node, is a closure failure.
        var allIds = new HashSet<string>(jnodes.Select(n => (string)n["id"]));
        var adj = new Dictionary<string, HashSet<string>>();
        foreach (var id in allIds) adj[id] = new HashSet<string>();
        foreach (var l in jlinks)
        {
            var s = (string)l["source"]; var t = (string)l["target"];
            if (!adj.ContainsKey(s)) adj[s] = new HashSet<string>();
            if (!adj.ContainsKey(t)) adj[t] = new HashSet<string>();
            adj[s].Add(t); adj[t].Add(s);
        }
        var seen = new HashSet<string>();
        var components = new List<List<string>>();
        foreach (var start in allIds)
        {
            if (seen.Contains(start)) continue;
            var comp = new List<string>();
            var stack = new Stack<string>(); stack.Push(start); seen.Add(start);
            while (stack.Count > 0)
            {
                var cur = stack.Pop(); comp.Add(cur);
                foreach (var nb in adj[cur]) if (seen.Add(nb)) stack.Push(nb);
            }
            components.Add(comp);
        }
        var degreeZero = allIds.Where(id => adj[id].Count == 0).OrderBy(x => x).ToList();
        components = components.OrderByDescending(c => c.Count).ToList();
        bool closed = components.Count <= 1 && degreeZero.Count == 0;

        // ---- frontier.json ----
        var frontier = new Dictionary<string, object>
        {
            ["defined"] = defined.Keys.OrderBy(x => x).ToList(),
            ["frontier_procs"] = frontierProcs,
            ["frontier_tables"] = frontierTables,
            ["stats"] = new Dictionary<string, object>
            {
                ["nodes"] = jnodes.Count, ["object_nodes"] = nodes.Count, ["edges"] = jlinks.Count, ["object_edges"] = edgeSet.Count,
                ["defined"] = defined.Count, ["frontier_procs"] = frontierProcs.Count, ["frontier_tables"] = frontierTables.Count,
                ["join_keys"] = joinKeys.Count, ["parse_fail"] = parseFail, ["components"] = components.Count, ["degree_zero"] = degreeZero.Count,
                ["closed"] = closed
            }
        };
        File.WriteAllText(frontOut, JsonSerializer.Serialize(frontier, new JsonSerializerOptions { WriteIndented = true }));

        // ---- lineage.json ----
        // `update set tgt.col = src.col`. dep view = stable source (real table or named CTE) -> real
        // target, self excluded (redundant for dep-mapping). everything kept here with self/dep flags;
        // bare per-query aliases (derived) drop out of the dep view.
        var allLin = lineage.Where(e => !Noise(e.srcTbl) && !Noise(e.tgtTbl)).Distinct().ToList();
        bool IsDep((string srcTbl, string srcCol, string tgtTbl, string tgtCol) e) =>
            e.srcTbl != e.tgtTbl && realTables.Contains(e.tgtTbl) && (realTables.Contains(e.srcTbl) || cteNames.Contains(e.srcTbl));
        var dep = allLin.Where(IsDep).ToList();
        var selfLin = allLin.Count(e => e.srcTbl == e.tgtTbl);
        var aliasDrop = allLin.Count - dep.Count - selfLin;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, "lineage.json"),
            JsonSerializer.Serialize(allLin.Select(e => new { src = e.srcTbl + "." + e.srcCol, tgt = e.tgtTbl + "." + e.tgtCol, self = e.srcTbl == e.tgtTbl, dep = IsDep(e) }),
                new JsonSerializerOptions { WriteIndented = true }));

        // ---- console report ----
        Console.WriteLine($"corpus: {files.Length} files  ->  nodes:{nodes.Count} edges:{edgeSet.Count} defined:{defined.Count}  (parse-fail after fallback: {parseFail})  [dialect: {dialect}]");
        Console.WriteLine($"FRONTIER: {frontierProcs.Count} undefined procs, {frontierTables.Count} undefined tables (the spider's next pull)");
        Console.WriteLine($"wrote {outPath}");
        Console.WriteLine($"wrote {frontOut}");
        if (graphifyPath != null)
        {
            var vocab = graphifyStandard ? "graphify's standard relation vocab" : "our native vocab (read/write/fk preserved)";
            Console.WriteLine($"wrote {graphifyPath}  (graphify format, {vocab})");
            Console.WriteLine($"  -> hand to graphify:  graphify cluster-only \"{dir}\"   (or  /graphify)");
        }

        Console.WriteLine("\n=== heaviest columns (alias-resolved, the weight) ===");
        foreach (var kv in colCount.OrderByDescending(k => k.Value).Take(12)) Console.WriteLine($"  {kv.Value,4}  {kv.Key}");

        Console.WriteLine("\n=== join keys (columns shared across 3+ real tables = the relationships) ===");
        foreach (var kv in joinKeys.OrderByDescending(k => k.Value.Count).Take(12))
            Console.WriteLine($"  {kv.Key}  ({kv.Value.Count} tables): {string.Join(", ", kv.Value.Take(6))}");

        Console.WriteLine($"\n=== COLUMN LINEAGE: {dep.Count} table->table dep edges  ({selfLin} self + {aliasDrop} via unresolved/derived aliases = detail-only in lineage.json) ===");
        foreach (var e in dep.Take(12)) Console.WriteLine($"  {e.srcTbl}.{e.srcCol}  ->  {e.tgtTbl}.{e.tgtCol}");

        // ---- the audit that gates closure ----
        Console.WriteLine($"\n=== CONNECTED-COMPONENTS AUDIT (success condition: 1 component, 0 orphans) ===");
        Console.WriteLine($"  components: {components.Count}   degree-0 nodes: {degreeZero.Count}");
        if (closed)
        {
            Console.WriteLine("  OK: single connected component, no orphans. Closure holds.");
            return 0;
        }
        Console.WriteLine("  FAIL: closure is NOT complete.");
        if (degreeZero.Count > 0)
            Console.WriteLine($"    degree-0 (isolated) nodes: {string.Join(", ", degreeZero.Take(20))}{(degreeZero.Count > 20 ? " ..." : "")}");
        if (components.Count > 1)
        {
            Console.WriteLine($"    {components.Count} disconnected components (largest first):");
            foreach (var c in components.Take(10))
                Console.WriteLine($"      [{c.Count} nodes] {string.Join(", ", c.OrderBy(x => x).Take(8))}{(c.Count > 8 ? " ..." : "")}");
        }
        Console.WriteLine("  -> pull the missing definitions (generate/reverse) and re-extract until this passes.");
        return 2;   // nonzero so the closure check can gate a build / CI step
    }

    // ---- graphify projection -------------------------------------------------
    // Write the same nodes/edges in graphify's graph.json schema: top-level nodes/edges/hyperedges,
    // with confidence_score added. graphify ingests arbitrary relation strings, so by DEFAULT we
    // keep our richer native vocab (fk / reads-references / writes / calls / join_key) -- the
    // read-vs-write split is the most useful provenance signal and graphify carries the label
    // through. Pass standardVocab=true (--graphify-standard) to collapse onto graphify's blessed
    // enum instead (references / calls / shares_data_with), e.g. when merging many DBs and you
    // want one uniform vocabulary across them.
    static void WriteGraphify(string path, List<Dictionary<string, object>> jnodes, List<Dictionary<string, object>> jlinks, bool standardVocab)
    {
        var gnodes = jnodes.Select(n => new Dictionary<string, object>
        {
            ["id"] = n["id"], ["label"] = n["label"],
            ["file_type"] = n["file_type"], ["source_file"] = n["source_file"]
        }).ToList();
        var gedges = jlinks.Select(l => new Dictionary<string, object>
        {
            ["source"] = l["source"], ["target"] = l["target"],
            ["relation"] = standardVocab ? GraphifyRelation((string)l["relation"]) : (string)l["relation"],
            ["confidence"] = "EXTRACTED", ["confidence_score"] = 1.0,
            ["source_file"] = l["source_file"], ["weight"] = l["weight"]
        }).ToList();
        var g = new Dictionary<string, object>
        {
            ["nodes"] = gnodes, ["edges"] = gedges, ["hyperedges"] = new List<object>(),
            ["input_tokens"] = 0, ["output_tokens"] = 0
        };
        var d = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(d)) Directory.CreateDirectory(d);
        File.WriteAllText(path, JsonSerializer.Serialize(g, new JsonSerializerOptions { WriteIndented = false }));
    }

    // sql-spider's relation vocabulary -> graphify's fixed enum.
    //   calls (proc/func calls) -> calls
    //   join_key (shared column) -> shares_data_with   (a column shared across tables IS shared data)
    //   fk / references / writes / anything else -> references
    static string GraphifyRelation(string rel) => rel switch
    {
        "calls" => "calls",
        "join_key" => "shares_data_with",
        _ => "references"
    };
}
