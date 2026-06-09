using SqlSpider;

// sql-spider: deterministic T-SQL dependency graph builder + spider-to-closure orchestrator.
//
// One CLI, several subcommands:
//   extract  <corpus-dir> [graph.json] [frontier.json]   parse *.sql -> dependency graph + frontier + audits
//   seed     <root-object> <outdir>                       cold-start pull queries for one root
//   generate <frontier.json> <outdir>                     forward pull queries for the current frontier
//   reverse  <referencers.csv> <outdir> [--roots a b]     reverse-referencer module-def pulls
//   absorb   <csv...> --corpus <dir>                       fold pulled CSVs back into the corpus as .sql
//   viz      <graph.json> [out.html]                       render graph.json to a standalone interactive HTML
//
// Data access is a Bring-Your-Own-adapter contract: the tool EMITS read-only queries and CONSUMES
// CSVs. It never opens a database connection itself. You run the emitted query against your database
// (sqlcmd / Invoke-Sqlcmd / your own driver) and drop the result CSV where the tool expects it.
//
// ---------------------------------------------------------------------------
// This file is the CLI / executable ONLY. It owns just two things:
//   1. arg routing (the Cli class), and
//   2. the dialect-picking wiring (Extractor.Pick) that maps --dialect to a concrete parser.
// EVERYTHING else -- the dialect-neutral engine (SqlSpider.Engine), the orchestrator, viz, csv,
// and the IDialectExtractor / CorpusFacts fact contract -- lives in SqlSpider.Core, which depends
// on NO SQL parser at all. The two concrete parsers live in their own assemblies
// (SqlSpider.TSql -> ScriptDom, SqlSpider.Generic -> SqlParserCS). This file is the ONE place
// allowed to name both concrete parsers, because Pick is the only code that constructs them.
// ---------------------------------------------------------------------------

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }
        var cmd = args[0];
        var rest = args.Skip(1).ToArray();
        try
        {
            switch (cmd)
            {
                case "extract":  return Extractor.Run(rest);
                case "seed":     return Orchestrator.Seed(rest);
                case "generate": return Orchestrator.Generate(rest);
                case "reverse":  return Orchestrator.Reverse(rest);
                case "absorb":   return Orchestrator.Absorb(rest);
                case "viz":      return Viz.Run(rest);
                case "round":    return Round(rest);
                case "-h": case "--help": case "help": Usage(); return 0;
                default: Console.Error.WriteLine($"unknown command: {cmd}\n"); Usage(); return 1;
            }
        }
        catch (CliError e) { Console.Error.WriteLine("error: " + e.Message); return 1; }
    }

    static void Usage()
    {
        Console.WriteLine(@"sql-spider  -  deterministic T-SQL dependency graph + spider-to-closure

usage:
  sql-spider extract  <corpus-dir> [graph.json] [frontier.json] [--dialect tsql|sqlite] [--graphify [out.json]] [--graphify-standard]
  sql-spider seed     <root-object> <outdir>
  sql-spider generate <frontier.json> <outdir>
  sql-spider reverse  <referencers.csv> <outdir> [--roots a b ...]
  sql-spider absorb   <csv> [csv ...] --corpus <dir>
  sql-spider viz      <graph.json> [out.html]

the loop (one pass):
  1. sql-spider extract  corpus/ graph.json frontier.json
  2. sql-spider generate frontier.json stage/
  3. run each emitted query against your database, save results as CSV into stage/
  4. sql-spider absorb  stage/*.csv --corpus corpus/
  5. goto 1   (the frontier shrinks each pass; done when it is empty and the graph is one component)

or one reviewable round at a time:
  sql-spider round <corpus-dir>   # extract -> graph + viz -> stage the next pull queries (or report closure)
                                   # review the viz, run the staged queries, absorb, then round again.");
    }

    // ROUND: one reviewable spider pass. extract the corpus -> write graph.json + a graph.html viz ->
    // read the frontier and, if anything is still referenced-but-undefined, stage the next pull queries.
    // default is ONE round so you can open the viz and decide whether to keep going; the closure-gate
    // exit code from extract is expected to be non-zero mid-loop (still unclosed), so we drive off the
    // frontier, not the exit code.
    static int Round(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: round <corpus-dir> [--dialect tsql|sqlite]");
            Console.Error.WriteLine("  one spider round: extract -> graph + viz -> stage the next pull queries (or report closure).");
            return 1;
        }
        var corpus      = args[0];
        var dialectArgs = args.Skip(1).ToArray();
        var graphPath   = Path.Combine(corpus, "graph.json");
        var frontPath   = Path.Combine(corpus, "frontier.json");
        var vizPath     = Path.Combine(corpus, "graph.html");
        var nextDir     = Path.Combine(corpus, "_next");

        var auditRc = Extractor.Run(new[] { corpus, graphPath, frontPath }.Concat(dialectArgs).ToArray());
        Viz.Run(new[] { graphPath, vizPath });

        var (procs, tables) = FrontierCounts(frontPath);
        Console.WriteLine();
        Console.WriteLine("=== round complete ===");
        Console.WriteLine($"graph: {graphPath}");
        Console.WriteLine($"viz:   {vizPath}   <- open this to review the round");
        if (procs + tables == 0)
        {
            Console.WriteLine("frontier is EMPTY -> nothing left to pull (see the audit above for closure).");
            // the FINAL round's exit code is the closure audit's verdict -- an empty frontier with a
            // disconnected graph must not report success to a CI gate.
            return auditRc;
        }
        Orchestrator.Generate(new[] { frontPath, nextDir });
        Console.WriteLine($"frontier: {procs} procs + {tables} tables still to pull -> queries staged in {nextDir}/");
        Console.WriteLine($"next: run those against your db, then  dotnet run -- absorb {nextDir}/*.csv --corpus {corpus}");
        Console.WriteLine($"      then  dotnet run -- round {corpus}  again.");
        return 0;
    }

    static (int procs, int tables) FrontierCounts(string frontPath)
    {
        if (!File.Exists(frontPath)) return (0, 0);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(frontPath));
        int p = 0, t = 0;
        if (doc.RootElement.TryGetProperty("frontier_procs", out var pp) && pp.ValueKind == System.Text.Json.JsonValueKind.Array) p = pp.GetArrayLength();
        if (doc.RootElement.TryGetProperty("frontier_tables", out var tt) && tt.ValueKind == System.Text.Json.JsonValueKind.Array) t = tt.GetArrayLength();
        return (p, t);
    }
}

// ---------------------------------------------------------------------------
// extract dispatch: the ONLY dialect-aware code in the whole tool.
// ---------------------------------------------------------------------------
// Pick is the dialect registry -- the one place that knows which concrete parsers exist. It maps
// a --dialect name to a concrete IDialectExtractor (TSqlExtractor from SqlSpider.TSql, or
// GenericSqlExtractor from SqlSpider.Generic). The dialect-neutral engine (SqlSpider.Engine) does
// ALL the real work; the CLI just hands it Pick as a delegate so the engine never has to name a
// concrete parser. Add a dialect here (and reference its assembly) and the engine is untouched.
static class Extractor
{
    // dialect registry: the only place that knows which parsers exist. add a dialect by
    // implementing IDialectExtractor (or enabling another SqlParserCS dialect) and adding a case.
    static IDialectExtractor Pick(string dialect) => dialect switch
    {
        "tsql"   => new TSqlExtractor(),
        "sqlite" => GenericSqlExtractor.Sqlite(),
        _ => throw new CliError($"unknown dialect '{dialect}' (known: tsql, sqlite)")
    };

    // hand the picked-parser delegate to the dialect-neutral engine; it does the rest.
    public static int Run(string[] args) => Engine.RunExtract(args, Pick);
}
