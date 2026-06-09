using System.Text;
using System.Text.Json;

namespace SqlSpider;

// ---------------------------------------------------------------------------
// orchestrator: emit read-only pull queries (seed / generate / reverse) and
// absorb the CSVs that come back into the corpus as .sql.
//
// Bring-Your-Own-adapter contract: this code NEVER opens a database connection.
// It writes .sql files and prints an instruction to run them against your database
// (sqlcmd / Invoke-Sqlcmd / your own driver) and save each result as a CSV.
// ---------------------------------------------------------------------------
public static class Orchestrator
{
    // chunked module definitions: substring slices of sys.sql_modules.definition to dodge driver
    // char-limits (some drivers cap a single column at ~4000 chars; big legacy procs overflow it).
    const int Chunks = 200;     // 200 * 4000 = 800k chars of definition reassembled on absorb
    const int Chunk  = 4000;

    // boring read-only safety preamble: no locks, be the deadlock victim, quiet.
    const string Guard =
        "-- read-only: honor no locks, be the deadlock victim if needed\n" +
        "set transaction isolation level read uncommitted\n" +
        "set deadlock_priority -10\n" +
        "set nocount on\n\n";

    static string InList(IEnumerable<string> names) =>
        string.Join(", ", names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToLowerInvariant())
            .Distinct().OrderBy(x => x).Select(n => "'" + n.Replace("'", "''") + "'"));

    // -------- SQL query templates (standard SQL Server system views) --------

    // full module text for procs/views/functions/triggers, chunked to dodge per-column truncation.
    static string QProcDefs(IEnumerable<string> procs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Chunks; i++)
            sb.Append($"    , [d{i:D2}] = substring(m.definition, {i * Chunk + 1}, {Chunk})\n");
        return Guard + "select\n" +
            "      [object_name] = o.name\n" +
            "    , [object_type] = o.type_desc\n" +
            sb +
            "from\n" +
            "  sys.sql_modules m\n" +
            "  join sys.objects o on\n" +
            "    o.object_id = m.object_id\n" +
            "where\n" +
            $"  o.name in ({InList(procs)})\n" +
            "order by\n" +
            "  o.name\n";
    }

    static string QTableSchema(IEnumerable<string> tables) =>
        Guard + "select\n" +
        "      [table_name]  = c.table_name\n" +
        "    , [column_name] = c.column_name\n" +
        "    , [data_type]   = c.data_type\n" +
        "    , [max_length]  = c.character_maximum_length\n" +
        "    , [is_nullable] = c.is_nullable\n" +
        "    , [ordinal]     = c.ordinal_position\n" +
        "from\n" +
        "  information_schema.columns c\n" +
        "where\n" +
        $"  c.table_name in ({InList(tables)})\n" +
        "order by\n" +
        "  c.table_name, c.ordinal_position\n";

    // real declared FK edges touching in-scope tables (schemas are often FK-sparse, but pull what exists).
    static string QFkEdges(IEnumerable<string> tables)
    {
        var list = InList(tables);
        return Guard + "select\n" +
            "      [fk_table]  = tp.name\n" +
            "    , [fk_column] = cp.name\n" +
            "    , [pk_table]  = tr.name\n" +
            "    , [pk_column] = cr.name\n" +
            "from\n" +
            "  sys.foreign_keys fk\n" +
            "  join sys.foreign_key_columns fkc on\n" +
            "    fkc.constraint_object_id = fk.object_id\n" +
            "  join sys.tables tp on\n" +
            "    tp.object_id = fk.parent_object_id\n" +
            "  join sys.columns cp on\n" +
            "    cp.object_id = fkc.parent_object_id\n" +
            "    and cp.column_id = fkc.parent_column_id\n" +
            "  join sys.tables tr on\n" +
            "    tr.object_id = fk.referenced_object_id\n" +
            "  join sys.columns cr on\n" +
            "    cr.object_id = fkc.referenced_object_id\n" +
            "    and cr.column_id = fkc.referenced_column_id\n" +
            "where\n" +
            $"  tp.name in ({list})\n" +
            $"  or tr.name in ({list})\n" +
            "order by\n" +
            "  tp.name, cp.name\n";
    }

    // what code references the root objects -> how the spider "follows everything around" a root.
    // server-side dependency tracking; misses dynamic sql, but cheap + catches code we don't have at all.
    static string QReverseRoots(IEnumerable<string> roots) =>
        Guard + "select distinct\n" +
        "      [referencing]      = o.name\n" +
        "    , [referencing_type] = o.type_desc\n" +
        "    , [referenced]       = d.referenced_entity_name\n" +
        "from\n" +
        "  sys.sql_expression_dependencies d\n" +
        "  join sys.objects o on\n" +
        "    o.object_id = d.referencing_id\n" +
        "where\n" +
        $"  d.referenced_entity_name in ({InList(roots)})\n" +
        "order by\n" +
        "  d.referenced_entity_name, o.name\n";

    // object-existence probe: does this name exist, and is it a module (has a definition) or a table?
    static string QObjectExists(IEnumerable<string> names) =>
        Guard + "select\n" +
        "      [object_name] = o.name\n" +
        "    , [object_type] = o.type_desc\n" +
        "    , [has_module]  = case when m.object_id is null then 0 else 1 end\n" +
        "from\n" +
        "  sys.objects o\n" +
        "  left join sys.sql_modules m on\n" +
        "    m.object_id = o.object_id\n" +
        "where\n" +
        $"  o.name in ({InList(names)})\n" +
        "order by\n" +
        "  o.name\n";

    static string Stamp() => DateTime.Now.ToString("yyyyMMddHHmmss");

    static void Emit(string outdir, string purpose, string sql, string note, List<string> written)
    {
        Directory.CreateDirectory(outdir);
        var path = Path.Combine(outdir, $"{Stamp()}-{purpose}.sql");
        File.WriteAllText(path, sql);
        written.Add(path);
        Console.WriteLine($"  wrote {path}   ({note})");
    }

    static void RunInstruction(List<string> written, string outdir)
    {
        Console.WriteLine("\n# next: run each query against your database, save the result as CSV into the outdir.");
        Console.WriteLine("# use sqlcmd, Invoke-Sqlcmd, or your own read-only adapter -- the tool never connects itself.");
        Console.WriteLine("# example shape (adapt to your adapter):");
        foreach (var p in written)
        {
            var csv = Path.Combine(outdir, Path.GetFileNameWithoutExtension(p) + ".csv");
            Console.WriteLine($"#   <your-adapter>  --query {p}  --out {csv}");
        }
        Console.WriteLine("\n# then fold the CSVs back in:");
        Console.WriteLine($"#   dotnet run -- absorb {Path.Combine(outdir, "*.csv")} --corpus <corpus-dir>");
    }

    // ----- seed: cold start from a single root on a fresh (empty) corpus -----
    public static int Seed(string[] args)
    {
        if (args.Length < 2) throw new CliError("usage: seed <root-object> <outdir>");
        var root = args[0].Trim().ToLowerInvariant();
        var outdir = args[1];
        var roots = new[] { root };
        Console.WriteLine($"# COLD START, root '{root}' -> {outdir}\n");
        var written = new List<string>();
        Emit(outdir, "SeedDef",    QProcDefs(roots),     "root's own module def (proc/view/func)", written);
        Emit(outdir, "SeedSchema", QTableSchema(roots),  "root's schema (if a table)", written);
        Emit(outdir, "SeedRefs",   QReverseRoots(roots), $"everything referencing {root}", written);
        RunInstruction(written, outdir);
        return 0;
    }

    // ----- generate: forward pull for the current frontier -----
    public static int Generate(string[] args)
    {
        if (args.Length < 2) throw new CliError("usage: generate <frontier.json> <outdir>");
        var frontierPath = args[0];
        var outdir = args[1];
        if (!File.Exists(frontierPath)) throw new CliError($"frontier file not found: {frontierPath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(frontierPath));
        var root = doc.RootElement;
        var procs  = ReadStringArray(root, "frontier_procs");
        var tables = ReadStringArray(root, "frontier_tables");

        Console.WriteLine($"# frontier pull -> {outdir}  ({procs.Count} procs, {tables.Count} tables)\n");
        if (procs.Count == 0 && tables.Count == 0)
        {
            Console.WriteLine("  frontier is empty -- nothing to pull. If the graph is also a single component, you're closed.");
            return 0;
        }
        var written = new List<string>();
        if (procs.Count > 0)  Emit(outdir, "ProcDefs",    QProcDefs(procs),     $"{procs.Count} procs", written);
        if (tables.Count > 0) Emit(outdir, "TableSchema", QTableSchema(tables), $"{tables.Count} tables", written);
        if (tables.Count > 0) Emit(outdir, "FkEdges",     QFkEdges(tables),     "declared fks", written);
        RunInstruction(written, outdir);
        return 0;
    }

    // ----- reverse: pull defs of objects that reference the given roots (1 level) -----
    public static int Reverse(string[] args)
    {
        if (args.Length < 2) throw new CliError("usage: reverse <referencers.csv> <outdir> [--roots a b ...]");
        var csvPath = args[0];
        var outdir = args[1];
        var roots = new HashSet<string>();
        int max = 0;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--roots") { while (i + 1 < args.Length && !args[i + 1].StartsWith("--")) roots.Add(args[++i].Trim().ToLowerInvariant()); }
            else if (args[i] == "--max" && i + 1 < args.Length) int.TryParse(args[++i], out max);
        }
        if (!File.Exists(csvPath)) throw new CliError($"referencers csv not found: {csvPath}");

        var refs = new SortedSet<string>();
        foreach (var r in Csv.Read(csvPath))
        {
            var referenced = (r.GetValueOrDefault("referenced") ?? "").ToLowerInvariant();
            if (roots.Count == 0 || roots.Contains(referenced))
            {
                var name = (r.GetValueOrDefault("referencing") ?? "").Trim().ToLowerInvariant();
                if (name.Length > 0) refs.Add(name);
            }
        }
        var list = refs.ToList();
        if (max > 0 && list.Count > max) list = list.Take(max).ToList();
        Console.WriteLine($"reverse sweep (1 level): {list.Count} objects referencing {(roots.Count > 0 ? string.Join(",", roots) : "all roots")}");
        if (list.Count == 0) { Console.WriteLine("  nothing to pull."); return 0; }
        var written = new List<string>();
        Emit(outdir, "RevDefs", QProcDefs(list), $"{list.Count} referencer module defs", written);
        RunInstruction(written, outdir);
        return 0;
    }

    // ----- absorb: fold pulled CSVs back into the corpus as .sql -----
    public static int Absorb(string[] args)
    {
        string? corpus = null;
        var csvs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--corpus" && i + 1 < args.Length) corpus = args[++i];
            else csvs.Add(args[i]);
        }
        if (corpus == null) throw new CliError("usage: absorb <csv> [csv ...] --corpus <dir>");
        if (csvs.Count == 0) throw new CliError("absorb: no CSV files given");
        Directory.CreateDirectory(corpus);

        int wrote = 0;
        foreach (var path in csvs)
        {
            if (!File.Exists(path)) { Console.WriteLine($"  {path}: not found"); continue; }
            if (new FileInfo(path).Length == 0) { Console.WriteLine($"  {path}: empty (no rows)"); continue; }
            var rows = Csv.Read(path);
            if (rows.Count == 0) { Console.WriteLine($"  {path}: no rows"); continue; }
            var hdr = new HashSet<string>(rows[0].Keys);

            if (hdr.Contains("object_name") && hdr.Any(k => k.StartsWith("d0")))   // chunked module defs
            {
                int n = 0;
                foreach (var r in rows)
                {
                    var name = (r.GetValueOrDefault("object_name") ?? "").Trim().ToLowerInvariant();
                    if (name.Length == 0) continue;
                    var sb = new StringBuilder();
                    for (int i = 0; i < Chunks; i++) sb.Append(r.GetValueOrDefault($"d{i:D2}") ?? "");
                    var body = sb.ToString();
                    if (body.Trim().Length == 0) continue;
                    File.WriteAllText(Path.Combine(corpus, SafeName(name) + ".sql"), body);
                    wrote++; n++;
                }
                Console.WriteLine($"  {path}: absorbed {n} module definitions");
            }
            else if (hdr.Contains("table_name") && hdr.Contains("column_name"))    // table schema -> synth create table
            {
                var tbls = new Dictionary<string, List<Dictionary<string, string>>>();
                foreach (var r in rows)
                {
                    var t = (r.GetValueOrDefault("table_name") ?? "").Trim().ToLowerInvariant();
                    if (t.Length == 0) continue;
                    (tbls.TryGetValue(t, out var l) ? l : tbls[t] = new()).Add(r);
                }
                foreach (var (t, crows) in tbls)
                {
                    crows.Sort((a, b) => Ord(a).CompareTo(Ord(b)));
                    var cols = new List<string>();
                    foreach (var r in crows)
                    {
                        var dt = (r.GetValueOrDefault("data_type") ?? "varchar").Trim().ToLowerInvariant();
                        var ml = (r.GetValueOrDefault("max_length") ?? "").Trim();
                        if (ml.Length > 0 && (dt is "varchar" or "nvarchar" or "char" or "nchar")) dt = $"{dt}({ml})";
                        var nullable = (r.GetValueOrDefault("is_nullable") ?? "").Trim().ToUpperInvariant();
                        var nn = (nullable is "YES" or "TRUE" or "1") ? "" : " not null";
                        var cn = (r.GetValueOrDefault("column_name") ?? "").Trim().ToLowerInvariant();
                        cols.Add($"    , {cn} {dt}{nn}");
                    }
                    // first column has no leading comma
                    var body = $"create table {t} (\n      " + (cols.Count > 0 ? string.Join("\n", cols)[6..] : "") + "\n)\n";
                    File.WriteAllText(Path.Combine(corpus, SafeName(t) + ".sql"), body);
                    wrote++;
                }
                Console.WriteLine($"  {path}: absorbed schema for {tbls.Count} tables");
            }
            else if (hdr.Contains("fk_table"))                                     // fk edges -> sidecar json
            {
                File.AppendAllText(Path.Combine(corpus, "_fk_edges.json"), JsonSerializer.Serialize(rows) + "\n");
                Console.WriteLine($"  {path}: recorded {rows.Count} fk edges (-> _fk_edges.json)");
            }
            else if (hdr.Contains("referencing"))                                  // reverse roots -> proc list to pull next
            {
                var names = new SortedSet<string>(rows.Select(r => (r.GetValueOrDefault("referencing") ?? "").Trim().ToLowerInvariant()).Where(s => s.Length > 0));
                File.WriteAllText(Path.Combine(corpus, "_referencers.json"), JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"  {path}: {names.Count} root referencers (-> _referencers.json; feed to the next reverse pull)");
            }
            else
            {
                Console.WriteLine($"  {path}: UNRECOGNIZED header [{string.Join(", ", hdr.OrderBy(x => x))}]");
            }
        }
        Console.WriteLine($"absorbed -> {wrote} new .sql files in {corpus}");
        return 0;
    }

    static int Ord(Dictionary<string, string> r) => int.TryParse(r.GetValueOrDefault("ordinal"), out var n) ? n : 0;
    static string SafeName(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));

    static List<string> ReadStringArray(JsonElement root, string prop)
    {
        var list = new List<string>();
        if (root.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
        return list;
    }
}
