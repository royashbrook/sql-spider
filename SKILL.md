---
name: sql-spider
description: >-
  Build a deterministic dependency graph of a SQL database (every table, view, stored procedure,
  function, and trigger, plus the edges between them) and spider outward from a seed object until
  the graph is CLOSED (one connected component, zero orphans). Use when the user wants to map what
  depends on what in a database: "map this database", "build a dependency graph of these procs",
  "what references table X", "what does proc Y touch", "find the closure around this table", "spider
  the schema", "I can't connect to the DB directly, give me queries I can run". The engine never
  opens a database connection: it emits READ-ONLY queries and ingests CSV results, so the agent is
  the adapter: you run each emitted query however you can reach the database (a connected SQL tool, a
  read-only query bridge, or by handing it to the user), feed the results back, and drive the loop to
  closure. Fully deterministic (parses SQL into an AST, reads dependencies off the tree), no live
  connection needed to build the graph. T-SQL and SQLite ship; other dialects are a small adapter.
  Optionally projects the closed graph into graphify's format (`--graphify`) so it plugs straight
  into graphify for community detection, query, path, and explain.
---

# sql-spider

Build a deterministic dependency graph of a SQL database and spider outward from a seed object
until it is **closed**: one connected component, zero orphans, no phantom edges. The graph engine
is pure and offline: it parses SQL text into an AST and reads the dependencies straight off the
tree, so the same input always yields the same graph, with no heuristics and no live connection.

**The thing this skill exists to solve: sql-spider never connects to a database.** It emits
read-only queries and ingests the CSV results. So *you, the agent, are the database adapter.* You
run each emitted query however you can reach the database, feed the results back, and drive the
loop. That is exactly how it was built (mapping a database reachable only through a read-only job,
never a direct connection), and it is why the tool is safe to point at anything.

## When to use this

- "Map this database" / "build me a dependency graph of these stored procs / tables / views."
- "What references `Orders`?" / "what does `proc_x` actually touch?"
- "Find the full closure around this table": everything it depends on, transitively, until nothing
  is left dangling.
- "I can't hit the database directly, emit the queries and I'll run them," or "send them to a DBA."
- Documenting / understanding a legacy schema where nobody knows what depends on what anymore.

Not for: writing application SQL, changing data or schema (this skill is **read-only**, see
Guardrails), or anything that needs the database to be mutated.

## How it works (the contract)

The engine is dialect-agnostic. The only dialect-specific step is parsing `.sql` into dependency
*facts* (behind a one-method `IDialectExtractor`). Everything else is dialect-neutral: graph
assembly, the frontier, the spider loop, the connected-components closure audit, lineage, `viz`.

The loop is always the same shape, regardless of dialect or database:

1. **seed / round** emits read-only `.sql` query files into an output directory.
2. *You* run those queries against the database (the adapter step) and save each result as a CSV.
3. **absorb** folds the CSVs back into the corpus as `.sql`.
4. **round** re-extracts, writes a graph + an HTML viz, and stages the next pull queries, or
   reports closure.
5. Repeat until the frontier is empty and the graph is one connected component.

## Step 0: build once (build-on-first-use)

This skill ships the .NET source, not a binary. The first invocation builds it; later invocations
reuse the build. Requires the **.NET 10 SDK** (`dotnet --version`; if absent, install it:
`brew install dotnet` on macOS, the distro package elsewhere, or https://dot.net).

Resolve `SKILL_DIR` to the directory containing this file, then define one command:

```sh
SKILL_DIR="<absolute path to this skill directory>"
spider() { dotnet run -c Release --project "$SKILL_DIR" -- "$@"; }
```

The first `spider ...` call compiles (a few seconds, restores ScriptDom + SqlParserCS); every call
after is fast. Sanity-check against the bundled example before touching a real database:

```sh
spider extract "$SKILL_DIR/examples/northwind"   # -> 36 nodes, 66 edges, "one component, zero orphans"
spider viz "$SKILL_DIR/examples/northwind/graph.json" && open "$SKILL_DIR/examples/northwind/graph.html"
```

If that prints `OK: single connected component, no orphans`, the tool is working.

## Step 1: pick the adapter (how will you reach the database?)

sql-spider emits queries; something has to run them. Decide which of these *you* are before
seeding (full recipes in [references/adapters.md](references/adapters.md)):

- **A connected SQL tool / MCP**: you have a tool that runs a query and returns rows. You run each
  emitted `.sql` and write the rows out as CSV. Fastest; best when the agent can reach the DB.
- **A read-only query bridge**: a job/skill where you drop a `.sql` file, it runs against the
  database, and a CSV comes back (this is the original setup, a CI job with read access). You
  dispatch each emitted query through it and collect the CSV.
- **The user runs them**: you can't reach the database at all. You print each query, the user runs
  it in their client (SSMS, `sqlcmd`, etc.), and pastes back the CSV. Slowest, but works anywhere
  and is the right move against a locked-down prod database (the user, or their DBA, runs in batches).

Whatever it is, the contract is identical: emitted `.sql` in, CSV out. Pick one and keep it for the
whole run.

## Step 2: set up a working directory

Work outside the skill directory so the skill repo stays clean. In the user's project (or any
scratch dir):

```sh
mkdir -p work/corpus work/stage && cd work
```

`corpus/` holds the accumulating `.sql` (the model so far); `stage/` is where this round's queries
and their CSVs live.

## Step 3: seed from a root object

Pick a root the user cares about, a central table or proc. Cold-start from it:

```sh
spider seed Orders stage/           # writes read-only pull queries into stage/
```

`seed` emits the queries to pull the root's own definition, its schema (if a table), and everything
that references it. Run those through your adapter (Step 1), save each result as a CSV in `stage/`,
then fold them in:

```sh
spider absorb stage/*.csv --corpus corpus/
```

## Step 4: round to closure (one reviewable round at a time)

```sh
spider round corpus/
```

Each round: re-extracts `corpus/` into `corpus/graph.json`, writes `corpus/graph.html` (open it to
review), and either reports closure or stages the next pull queries in `corpus/_next/`. If there is
a frontier left:

```sh
# run corpus/_next/*.sql through your adapter, save CSVs into corpus/_next/, then:
spider absorb corpus/_next/*.csv --corpus corpus/
spider round corpus/      # again
```

The frontier shrinks every round. **Default to one round at a time** so a human can open the viz
and decide whether to keep pulling, especially against production. Keep going until `round`
reports the frontier is empty.

## Step 5: closure

You are done when the frontier is empty **and** the graph is one connected component with zero
orphans. The closure audit is the gate; `extract` even returns a non-zero exit code while the graph
is still unclosed, so it can be wired into CI.

Not every object closes by edges, and that is fine: a genuinely standalone object (an app-only
function, a version/archive table referenced only from outside the database) is reported **with a
reason**, not as an error. The honest invariant is "zero orphans *from extraction misses*": when a
node is isolated, the audit tells you whether it is a real gap to pull or a true standalone.

## Step 6: view it, and (optional) hand off to graphify

You already have a viewer, no install needed: `spider viz <graph.json>` writes a self-contained
interactive HTML (force-directed, draggable, colored by kind). Open it in any browser. For most
uses that is all you need.

**If graphify is installed, offer to hand the closed graph to it.** graphify
(https://github.com/safishamsi/graphify) turns a graph into a queryable knowledge graph with
community detection and `query` / `path` / `explain`. As an agent, check whether it is available (a
`graphify` command on PATH, a `graphify` skill, or an existing `graphify-out/` in the project), and
if so, ask the user whether to also emit the graphify view and hand it over.

**Use `--graphify`, do not copy the native `graph.json` into graphify.** The native graph is a
directed multigraph; handed to graphify raw it gets under-read. The `--graphify` flag writes a
separate, graphify-shaped file and leaves the native `graph.json` untouched:

```sh
spider extract corpus/ --graphify     # writes native graph.json AND graphify-out/graph.json
graphify cluster-only corpus/          # cluster + report   (or just /graphify)
```

Two things worth knowing, both learned on a real run:

- **Relation vocab (default keeps ours).** The projection keeps sql-spider's own relations by
  default (`fk` / `references` / `writes` / `calls` / `join_key`). graphify ingests them fine, and
  the read-vs-write split is the most useful signal for "what actually updates this table." Add
  `--graphify-standard` to collapse onto graphify's blessed enum instead (`references` / `calls` /
  `shares_data_with`), which is handy when you are merging many databases and want one uniform vocab.
- **Asking how a table is used.** Reach for graphify `explain X`, not `query X`. A referenced table
  is a sink (edges point inward), so `query` (an outward walk) returns just the table itself, while
  `explain` shows the inbound edges that answer the question.

In our own use we mapped a billing subsystem to a closed graph and answered "what depends on this"
straight from it, opening only the one procedure the graph pointed at. graphify's own docs cover the
rest of what the knowledge graph buys you. If graphify is not installed, skip all this; `viz` already
shows the graph, and sql-spider's job is done once it is closed.

## Guardrails

- **Read-only, always.** Every emitted query is a `select` with a no-lock / deadlock-victim /
  `set nocount on` safety preamble. Never edit one into a write. Never run `insert` / `update` /
  `delete` / `merge` / DDL through this skill. If you are composing anything other than a read, you
  are using the wrong tool.
- **Against production, pace it.** Do not auto-fire a flood of queries at a prod database. Run one
  reviewable round at a time, or hand the staged queries to the user / a DBA to run in batches.
  Auto-looping (`round` until closed) is only appropriate when the adapter can safely run unattended
  and the database owner is fine with it.
- **The graph is the safer artifact than the bodies.** The dependency graph (object names + edges)
  is the durable output. The pulled proc *bodies* in `corpus/` are the target organization's
  internal logic. Treat that corpus as private; publish the graph, not the bodies.

## Subcommands

| command | what it does |
|---|---|
| `extract <corpus-dir> [graph.json] [frontier.json] [--dialect tsql\|sqlite] [--graphify [out.json]] [--graphify-standard]` | parse `*.sql` into a dependency graph + frontier + audits; `--graphify` also writes a graphify-format graph (`--graphify-standard` uses graphify's blessed relation vocab) |
| `seed <root-object> <outdir>` | cold-start pull queries for one root on an empty corpus |
| `generate <frontier.json> <outdir>` | forward-pull queries for the current frontier |
| `reverse <referencers.csv> <outdir> [--roots a b]` | pull module defs of objects that reference the roots (one reverse level) |
| `absorb <csv...> --corpus <dir>` | fold pulled CSVs back into the corpus as `.sql` |
| `viz <graph.json> [out.html]` | render a graph to one standalone interactive HTML file |
| `round <corpus-dir> [--dialect ...]` | one reviewable spider pass: extract → graph + viz → stage the next queries (or report closure) |

`round` is the loop driver; the others are the pieces it composes (and that you call directly for a
cold start with `seed`, or a manual pass with `generate` / `reverse`).

## Dialects

`--dialect tsql` (default) parses with Microsoft ScriptDom (full T-SQL fidelity). `--dialect sqlite`
parses with SqlParserCS (which also covers MySQL / Postgres / others). Adding a dialect is one small
class implementing `IDialectExtractor` registered in `Extractor.Pick`; the engine is untouched.
See [README.md](README.md) for the full design and the worked Northwind / Chinook / AdventureWorks
examples.
