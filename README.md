# sql-spider

[![ci](https://github.com/royashbrook/sql-spider/actions/workflows/ci.yml/badge.svg)](https://github.com/royashbrook/sql-spider/actions/workflows/ci.yml)

> **sql-spider is a drop-in agent skill: you point an agent at it and it maps your database.**
>
> **Fastest path (anyone):** paste this one line to your agent:
>
> > `Fetch https://raw.githubusercontent.com/royashbrook/sql-spider/main/SKILL.md and follow it to install and use sql-spider.`
>
> The agent fetches the instructions, installs the skill so it's there next time, builds the tool,
> and drives the read-only spider loop to a closed graph. No git knowledge needed on your end.
> (Equivalent in plain words: *"install this skill from https://github.com/royashbrook/sql-spider,
> then map my database starting from `Orders`."*) That's the whole intended use.
>
> **If you are an AGENT reading this README:** your entry point is [`SKILL.md`](SKILL.md), go
> there. First clone the repo into your skills directory so the capability persists
> (`git clone https://github.com/royashbrook/sql-spider ~/.claude/skills/sql-spider` for Claude
> Code; wherever your harness auto-discovers skills otherwise). Run-in-place works once and is
> forgotten; installed, every future session discovers it on its own, so *"use this tool"*
> includes installing it. For the graphify pairing, install graphify's *skill* too, not just its
> binary (two commands, see [Pairing with graphify](#pairing-with-graphify-and-a-quick-look); the
> second is the one everyone skips).
>
> **If you are a HUMAN:** you do not need to read the rest of this README. It is reference for
> when you (or your agent) want the details. The tool *can* be run by hand (it is a normal .NET
> CLI), but it was built to be DRIVEN by an agent: it emits read-only queries, something runs
> them against your database, the results feed back, round by round, until the graph closes. That
> loop is an agent's job, not a fun manual one. The tool never connects to a database itself; the
> agent (or you) is the adapter. So the move is the same one above: point your agent at it.

A deterministic SQL dependency-graph extractor and spider. The graph engine is
**dialect-agnostic**: only the "parse SQL into dependency facts" step is dialect-specific,
behind a one-method `IDialectExtractor` interface. T-SQL is parsed with
[Microsoft.SqlServer.TransactSql.ScriptDom](https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom)
(the same parser SQL Server itself uses) for full fidelity; other dialects (SQLite, and the
MySQL/Postgres/etc. that [SqlParserCS](https://www.nuget.org/packages/SqlParserCS/) supports) go
through a generic SqlParserCS-backed extractor. Either way it is fully deterministic, with no
heuristics-that-drift and no live database connection needed to parse: it turns the actual SQL
into an AST and reads the dependencies straight off the tree, so the same input always produces
the same graph.

It does two things:

1. **extract**: parse a directory of `.sql` files into a node-link dependency graph
   (`graph.json`), a frontier of referenced-but-undefined objects (`frontier.json`), and a set
   of self-audits. A single `.sql` file may define many objects (a full schema/install script,
   a migration); each defined object becomes its own node with its own correctly-scoped edges.
2. **spider**: seed one object, emit the read-only queries needed to pull its definition,
   schema, and referencers; you run those against your database and feed the results back. Each
   pass shrinks the frontier. You are done when the frontier is empty **and** the graph is a
   single connected component.

## The success condition

> Seed an object, iterate read-only pulls until the graph is **closed**: one connected
> component, **zero orphans**, no phantom edges.

The connected-components audit is the gate. `extract` returns a non-zero exit code while the
graph is unclosed (more than one component, or any degree-0 node), so you can wire it into a
build or CI step. When it prints `OK: single connected component, no orphans` you have a
complete, self-consistent dependency closure around your seed.

## Dialects

The headline design: **sql-spider is a dialect-agnostic dependency-graph engine.** Everything is
dialect-**neutral**: graph assembly, the frontier, the spider loop (seed/generate/reverse/absorb),
the connected-components closure audit, lineage roll-up, and `viz`. It works
on dependency *facts*, never on SQL text. The only dialect-specific step is turning `.sql` into
those facts, which lives behind one small interface:

```csharp
interface IDialectExtractor
{
    CorpusFacts Extract(IReadOnlyList<string> files);   // SQL text -> dependency facts
}
```

Two implementations ship:

- **`TSqlExtractor`**: T-SQL via Microsoft ScriptDom. Full fidelity: tables, views, procs,
  functions, triggers, declared foreign keys, alias-resolved column join-keys, and `UPDATE`-set
  column lineage. This is the default (`--dialect tsql`).
- **`GenericSqlExtractor`**: any dialect [SqlParserCS](https://www.nuget.org/packages/SqlParserCS/)
  understands. Wired here for **SQLite** (`--dialect sqlite`): it walks the parsed AST and emits
  facts for `CREATE TABLE` (+ its `FOREIGN KEY` constraints, table-level and inline), `CREATE VIEW`
  (+ the tables its query selects from), `CREATE TRIGGER` (+ the tables it reads/writes), and
  `CREATE INDEX` (the table it is on). SQLite has no stored procedures or SQL functions, so the
  call/function facts simply never appear, which is correct for the dialect, not a gap.

### Adding a dialect

Implement `IDialectExtractor` (or, for any dialect SqlParserCS already supports, reuse
`GenericSqlExtractor` with a different `Dialect`) and register it in `Extractor.Pick`. The rest of
the pipeline (graph, closure, frontier, viz) is untouched. The interface returns exactly the
facts the engine needs per file: the defined containers (name + kind), and per container its table
reads/writes, called procs/functions, FK edges, and (where the dialect supports it) column
join-keys and lineage.

> SqlParserCS API note: its SQLite dialect does not parse SQLite's `CREATE TRIGGER` syntax (it
> routes to a constraint-trigger parser and throws). `GenericSqlExtractor` isolates that: it
> re-parses statement-by-statement so one unparseable statement never sinks the file, and falls
> back to a small regex to still pull a trigger's name and the tables it touches.

## Project layout

The code is split into separate assemblies so the pluggable-parser strategy is structural, not
just a convention: **the parser is a separate module, and the engine never depends on a specific
SQL dialect.**

```
sql-spider.sln
sql-spider.csproj            CLI / executable: arg routing + the dialect pick (--dialect -> a
                             concrete parser). The ONE place allowed to name both parsers.
src/core/   SqlSpider.Core     the dialect-NEUTRAL engine: the IDialectExtractor interface +
                             CorpusFacts/FileFacts/Container fact types, graph assembly, the
                             frontier, join-key/lineage layers, the connected-components closure
                             audit, the seed/generate/reverse/absorb orchestrator, and viz.
                             References NO SQL parser at all.
src/tsql/   SqlSpider.TSql     the T-SQL parser module: TSqlExtractor + its ScriptDom visitors.
                             References Core + Microsoft.SqlServer.TransactSql.ScriptDom.
src/generic/SqlSpider.Generic  the generic/SQLite parser module: GenericSqlExtractor.
                             References Core + SqlParserCS.
```

`SqlSpider.Core` has **zero** parser package references. That is the load-bearing invariant. The
engine consumes the dependency *facts* an `IDialectExtractor` returns and never sees SQL text, so
it cannot depend on how that text was parsed. The CLI injects the picked parser into the engine as
a delegate; swap or add a parser module without recompiling the engine. (`dotnet run` from the
repo root still resolves to the root `sql-spider.csproj`, so every command below is unchanged.)

## Quick start (Northwind)

The repo ships with Microsoft's public Northwind sample database script
(`examples/northwind/northwind.sql`, MIT-licensed): one file that defines 13 tables, 16 views,
and 7 stored procedures. It is exactly the multi-object-per-file shape the extractor is built for.

Run it one step at a time:

**1. Build** (once).
```sh
dotnet build
```

**2. Extract**: parse the script into a dependency graph. The outputs land right in the example
folder, so it stays self-contained (and they're gitignored: they regenerate every run, the repo
only ships the `.sql`).
```sh
dotnet run -- extract examples/northwind          # writes graph.json + frontier.json into examples/northwind/
```

**3. Visualize**: render that graph to a standalone HTML and open it.
```sh
dotnet run -- viz examples/northwind/graph.json   # writes examples/northwind/graph.html
open examples/northwind/graph.html                # macOS; or open it in any browser
```

(`viz` reads the `graph.json` from any `extract`, so in the spider loop you can re-`viz` after each
round to watch the graph grow.)

You get 36 object nodes (13 tables, 16 views, 7 procs), 66 edges, and the connected-components
audit reports **one component, zero orphans: closure holds.** That includes the lookup/junction
tables (region, territories, the demographics tables) whose only links are declared foreign keys:
Northwind declares all of its FKs via `ALTER TABLE ... ADD CONSTRAINT`, and the extractor parses
those (and inline FK constraints) straight from the DDL, so a pure-static parse stays connected
with no live database needed.

## Quick start, second dialect (Chinook / SQLite)

`examples/sqlite/` ships [Chinook](https://github.com/lerocha/chinook-database), the SQLite
world's Northwind, as a schema-only DDL file (`chinook.sql`: 11 FK-connected `CREATE TABLE` +
11 `CREATE INDEX`; the original script's ~15k rows of `INSERT` data were stripped so the example
is fast and purely about the dependency graph). Point `extract` at it with `--dialect sqlite`:

```sh
dotnet run -- extract examples/sqlite --dialect sqlite
```

You get 11 table nodes and 10 foreign-key edges (the `Employee.ReportsTo` self-FK is dropped as a
self-edge), and the closure audit reports **one component, zero orphans**: every Chinook table is
FK-reachable. No procs/functions/columns appear, which is correct for SQLite. Same engine, same
audit, same `viz`, only the parser changed.

## See the loop (AdventureWorks, stepped)

Northwind closes in one pass; it doesn't show the *loop*. `examples/adventureworks/` does: a
stepped walk-through of the spider with **no live database**. It seeds on one table
(`Sales.SalesOrderHeader`) and stages each pull by hand, so you watch the frontier shrink ring
by ring (`7 tables → 6 tables + 1 function → 0`) until it closes:

```sh
bash examples/adventureworks/demo.sh
```

See `examples/adventureworks/README.md` for the prose walk-through, including how genuinely
standalone objects are reported with a reason rather than as an error.

## Subcommands

Invoke any subcommand with `dotnet run -- <subcommand> [args]`, no separate binary to run, `dotnet run`
compiles if needed then runs. (For a standalone `sql-spider` command, `dotnet publish -c Release` or
`dotnet tool install`.)

```
dotnet run -- extract  <corpus-dir> [graph.json] [frontier.json] [--dialect tsql|sqlite] [--graphify[=out.json]]
dotnet run -- seed     <root-object> <outdir>
dotnet run -- generate <frontier.json> <outdir>
dotnet run -- reverse  <referencers.csv> <outdir> [--roots a b ...]
dotnet run -- absorb   <csv> [csv ...] --corpus <dir>
dotnet run -- viz      <graph.json> [out.html]
```

- **extract**: parse `*.sql` in `<corpus-dir>` into `graph.json` + `frontier.json` + audits.
  `--dialect` selects the parser (`tsql` default, or `sqlite`); see [Dialects](#dialects).
- **seed**: cold start from a single root on an empty corpus: emit the pull queries for the
  root's own definition, its schema (if a table), and everything that references it.
- **generate**: emit forward-pull queries for the current frontier (undefined procs/tables).
- **reverse**: from a referencers CSV, pull the module definitions of objects that reference
  the given roots (one level of reverse dependency).
- **absorb**: fold pulled CSVs back into the corpus as `.sql` (module defs are reassembled
  from chunked columns; table schemas are synthesized into `create table` DDL).
- **viz**: render an extracted `graph.json` to one self-contained interactive HTML file
  (force-directed, draggable, nodes colored by kind, edge relations on hover). See below.

## The loop (one pass)

```sh
dotnet run -- extract  corpus/ graph.json frontier.json
dotnet run -- generate frontier.json stage/
# run each emitted query against your database, save results as CSV into stage/
dotnet run -- absorb   stage/*.csv --corpus corpus/
# repeat: the frontier shrinks each pass, done when it is empty and the graph is one component
```

## Bring-your-own-adapter

sql-spider **never opens a database connection.** It is a pure
emit-queries / ingest-CSVs tool, which keeps it driver-agnostic and safe to run anywhere:

- `generate` / `seed` / `reverse` write **read-only** `.sql` query files (each carries a
  no-lock, deadlock-victim, `set nocount on` safety preamble) into an output directory.
- You run those queries against your database with whatever adapter you like and save each
  result as a CSV.
- `absorb` reads those CSVs back into the corpus.

The CSV column contract is just the column names the emitted queries produce (`object_name` +
`d00..dNN` chunks for module defs, `table_name`/`column_name`/... for schema, `fk_table`/... for
FK edges, `referencing`/`referenced` for reverse deps). Any adapter that produces those columns
works. Two common ones:

```powershell
# PowerShell / Invoke-Sqlcmd (works everywhere PowerShell does, including Linux/macOS)
Invoke-Sqlcmd -ServerInstance <server> -Database <db> -InputFile stage\20240101-ProcDefs.sql |
  Export-Csv -NoTypeInformation -Encoding utf8 -Path stage\20240101-ProcDefs.csv
```

> Your adapter must emit **real (RFC-4180-style) CSV**: quoted fields, so embedded commas,
> quotes, and newlines survive. `Export-Csv` does this. Plain `sqlcmd -s ","` does **not** (it
> never quotes, prints a dashed separator row, and renders NULL as the literal word): module
> definitions round-tripped through it come back corrupted. If you use `sqlcmd`, post-process its
> output into proper CSV, or use any client library that writes quoted CSV.

## Pairing with graphify (and a quick look)

sql-spider stands on its own: it builds and closes the dependency graph, and `viz` (below) shows
it with zero install. We built it that way first. The graphify pairing came second, as a way to
plug the closed graph into a richer knowledge-graph tool, so it is an **opt-in switch**, not a
dependency.

Pass `--graphify` to *also* write a graphify-format graph alongside the native one. Use the flag,
do not copy the native `graph.json` into graphify (it is a directed multigraph and gets under-read
raw):

```sh
dotnet run -- extract corpus/ --graphify     # writes corpus/graph.json AND corpus/graphify-out/graph.json
graphify cluster-only corpus/                 # community detection + report   (graphify, installed separately)
```

The native `graph.json` is always written and unchanged. The `--graphify` file is a separate,
graphify-shaped copy of the same graph (top-level `nodes`/`edges`/`hyperedges`, `confidence_score`
added) that drops straight into [graphify](https://github.com/safishamsi/graphify)'s `cluster-only`
/ `query` / `path` / `explain` / `merge-graphs`. By default it **keeps sql-spider's own relation
vocabulary** (`fk` / `references` / `writes` / `calls` / `join_key`); graphify ingests arbitrary
relations fine, and the read-vs-write split is the most useful signal for "what actually writes this
table." Add `--graphify-standard` to collapse onto graphify's blessed enum instead
(`references` / `calls` / `shares_data_with`), which is handy when you are merging many databases and
want one uniform vocabulary. If you don't use graphify, ignore the flag.

graphify is installed separately, and **it is two commands, not one**:

```sh
uv tool install graphifyy            # the graphify binary
graphify install --platform claude   # registers graphify's own skill with your agent
```

The second command is the one everyone (humans and agents alike) skips. Without it your agent can
run graphify when told to, but will never reach for it on its own, because the skill registration
is what makes graphify a discoverable capability rather than just a binary on PATH. Swap
`--platform claude` for your harness if it isn't Claude Code.

> Tip: to ask graphify "how is table X used", use `graphify explain X`, not `query X`. A referenced
> table is a sink (its edges point inward), so an outward `query` returns just the table itself,
> while `explain` shows the inbound edges that answer the question.

If you just want a quick look without installing anything, `viz` renders the native `graph.json`
to a standalone HTML file:

```sh
dotnet run -- viz graph.json          # writes graph.html
dotnet run -- viz graph.json out.html  # or name the output
```

The output is one self-contained HTML file. Open it in any browser. It draws an interactive
force-directed graph (vis-network from a CDN, nothing to install): nodes colored by kind
(table / view / proc / function / trigger / column / script), draggable, with each edge's
relation shown on hover. The graph data is embedded directly in the file, so there's no server
and no separate data file to serve.

## Scope

This is the whole thing: a self-contained skill plus the .NET tool it drives. It is not a
component of any other project; you clone it and use it.

graphify interoperability is one-directional and opt-in: `extract --graphify` writes a
graphify-format projection alongside the native graph, so a closed graph drops straight into
graphify for clustering and query if you want to go further than the built-in `viz` (see
[Pairing with graphify](#pairing-with-graphify-and-a-quick-look)). There is no graphify-native port
of this tool and none is planned. The value is the pluggable-parser engine and the
spider-to-closure loop, delivered as a drop-in skill, not a contribution to another codebase.

**Names are database-scoped, not schema-scoped (a deliberate choice).** The extractor identifies
objects by bare name: `[dbo].[Foo]`, `dbo.Foo`, and `FOO` all resolve to one node, which is what
you want inside one database. The flip side: two same-named objects in *different* schemas
(`sales.orders` vs `archive.orders`) merge into one node. We kept it that way because the spider's
unit of work is one database reached through one adapter, and single-name identity keeps every
join (frontier, absorb, reverse) simple and predictable. If your map genuinely spans schemas or
databases, run the loop once per scope and combine afterward: the boundary edges of one closed
graph (references that leave the scope) are the *seeds* for the next scan, and the resulting
`graph.json` files merge on node id (`graphify merge-graphs a.json b.json` does exactly this, or
a few lines of json union). Not turnkey, but the bones are all here, and an agent driving the
skill can do it without new tooling.

## Tests

```sh
bash tests/run-tests.sh
```

End-to-end regression tests that drive the real CLI against small fixture corpora: the shipped
examples must close with their documented numbers, and each fixture encodes a specific fixed bug
(if one fails, that bug is back). CI runs the same script on every push.

## A real run (how we actually used it)

This tool started as a way to map a database we **couldn't connect to directly**: a
[TMW](https://transportation.trimble.com/) system (Trimble's transportation-management platform),
reachable only through a job that had read access. So the tool never connected: it emitted the
read-only queries, that job ran them and committed back CSVs, and we fed those in. The whole
bring-your-own-adapter design above is exactly that experience, generalized.

The run went in rounds. We seeded from a handful of core tables (the order, payment, and invoice
headers). Each round was the same: `extract` → read the frontier → `generate`/`reverse` the pull
queries → run them through the job → `absorb` the CSVs → `extract` again.

- Forward-closure from a curated set of procedures converged fast: undefined procs `90 → 23 → 8`
  over two passes.
- Reverse-sweeping the three root tables pulled in everything that referenced them; one of those
  tables alone is referenced by **~1,500 objects**.
- The full order/pay/invoice subsystem closed at roughly **3,000 objects / 17,000 edges in one
  connected component.**

Every gap the closure hit showed up as a degree-0 node or a disconnected component, and the
self-audit pointed straight at it. The ones we closed are each now baked into the tool:

- **Truncation**: the largest procedures blew past the query driver's character cap and came back
  clipped. Fix: pull each definition in `substring` chunks and reassemble; re-pull anything that hit
  the cap at a higher chunk count.
- **Legacy syntax**: procedures written in the 1990s (double-quoted string literals, old `*=`
  outer joins) that the modern parser rejects. Fix: fall back to the older T-SQL parser dialect when
  the current one errors.
- **FK-only relationships**: lookup tables whose only link is a declared foreign key. Fix: parse
  FK constraints straight from the DDL, including `ALTER TABLE ... ADD CONSTRAINT`.
- **Function calls**: scalar UDFs in expressions and table-valued functions in `FROM` weren't being
  recorded as edges. Fix: extract them too.

As a quick sanity check on the finished tool: seeding from a single table and pulling just 25 of its
~1,500 referencers produced a **94-node, single-connected-component** graph in two rounds. The loop
holds.

And the payoff, measured rather than guessed: building the structural layer originally pulled
**2,308 KB of raw SQL across 121 read-only queries (~580k tokens of material)**. Re-verifying every
structural claim in our internal documentation against the finished graph (62 claims) consumed
**~15 KB (~4k tokens) and zero new database queries, about 1% of the original cost.** That is the
working model: the graph is the *finder*. The pull cost is not an extra cost, you pay it anyway the
first time you analyze the database at all; the graph is what keeps you from paying it again on
every question. Dependency questions are answered from the graph, and you only open the underlying
SQL when the graph points you at the one object that matters.

For fun, here is the viz for the raw output that can be consumed by graphify:

<img width="1196" height="1136" alt="image" src="https://github.com/user-attachments/assets/01d2211a-5e06-4e22-9443-f633f7e276fb" />

And here it is after consumed by graphify:

<img width="1277" height="1216" alt="image" src="https://github.com/user-attachments/assets/4fb2d594-673c-4587-9931-428b2748bd54" />

This pictures are, as claude put it, 'equally beautiful and useless' for a person to look at. But they do illustrate the work that can be done and the raw data you can put into a format to make consumable by an agent.

By the time I created this for use with graphify on something else, I had already done all the work with this raw data just as text and sql code and had generated a significant corpus of data that an agent could consume. But I was able to validate the output of that using graphify as a skill (at greatly reduced token cost) and also do additional research which helped in a different way using this skill.


## Requirements

- .NET 10 SDK
- `Microsoft.SqlServer.TransactSql.ScriptDom` (T-SQL parser) and `SqlParserCS` (generic /
  SQLite parser), both restored automatically by `dotnet build`.

## License

MIT.
