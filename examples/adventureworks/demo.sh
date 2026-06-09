#!/usr/bin/env bash
# Stepped walk-through of the spider LOOP on a slice of AdventureWorks.
#
# IMPORTANT: this is a SIMULATION. sql-spider never connects to a database -- it EMITS the
# read-only queries and INGESTS the CSVs you feed back. So at each step below, the line marked
#   >>> RUN-ON-SERVER <<<
# is the point where, in real life, YOU would run the emitted queries against your own database
# and save the results. Here we just hand you the result (the next ring of .sql files) so you can
# watch the loop without a live database. The loop is identical every pass:
#   extract -> read the frontier -> generate the pull queries -> (run them on your server) ->
#   absorb the results -> extract again. Repeat until the frontier is empty = closure.
#
# usage:  bash examples/adventureworks/demo.sh   (run from the sql-spider repo root)
# all artifacts are left under examples/adventureworks/_demo-out/ for you to inspect:
#   cat examples/adventureworks/_demo-out/step1-stage/*.sql
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
cd "$root"

out="$here/_demo-out"
rm -rf "$out"; mkdir -p "$out"          # fresh each run; gitignored, kept for inspection

run() { echo "+ dotnet run -- $*"; dotnet run -- "$@"; }
frontier() { run extract "$1" "$2" "$3" | grep -E '^FRONTIER:' || true; }

echo
echo "############################################################"
echo "# STEP 0 - COLD START: you have NOTHING but a table name."
echo "############################################################"
echo "# You want to map Sales.SalesOrderHeader. You don't have its DDL yet -- so the very first"
echo "# thing the tool does is emit the read-only queries to GO GET it (its own definition, its"
echo "# schema, and everything that references it)."
echo
run seed salesorderheader "$out/step0-stage"
echo
echo "#   >>> RUN-ON-SERVER <<<  you run those .sql files against your database, save the CSVs,"
echo "#   and 'dotnet run -- absorb' them. The result of doing that = the DDL now in step1-seed/."
echo "#   (we provide it so you can keep going without a live db.)"

echo
echo "############################################################"
echo "# STEP 1 - SEED IN HAND: one table's DDL. Read the FRONTIER."
echo "############################################################"
echo "# Now extract sees SalesOrderHeader (its CREATE + declared FKs). The FRONTIER is the tables"
echo "# those FKs point at that we don't have yet = exactly the next DDL to pull."
echo
frontier examples/adventureworks/step1-seed "$out/step1-graph.json" "$out/step1-frontier.json"
echo
echo "# generate turns that frontier into the read-only pull queries:"
run generate "$out/step1-frontier.json" "$out/step1-stage"
echo
echo "#   >>> RUN-ON-SERVER <<<  run those against your db, absorb the CSVs. The result = step2-pulled/."
echo "#   (cat examples/adventureworks/_demo-out/step1-stage/*.sql to see the actual queries.)"

echo
echo "############################################################"
echo "# STEP 2 - FIRST PULL IN: + those 7 tables. SAME loop again."
echo "############################################################"
echo "# Those 7 are now DEFINED, so they leave the frontier -- but their OWN FKs introduce the"
echo "# next ring (stateprovince, currency, person, store, employee, countryregion). Smaller: 7 -> 6."
echo
frontier examples/adventureworks/step2-pulled "$out/step2-graph.json" "$out/step2-frontier.json"
echo
echo "# same step as before: generate the pull queries for the new frontier:"
run generate "$out/step2-frontier.json" "$out/step2-stage"
echo
echo "#   >>> RUN-ON-SERVER <<<  run, absorb. The result = step3-closed/."

echo
echo "############################################################"
echo "# STEP 3 - SECOND PULL IN: + the next ring -> CLOSURE."
echo "############################################################"
echo "# Their FKs only point at tables we already have, down to businessentity (the last leaf)."
echo "# Nothing is referenced-but-undefined. The frontier is EMPTY (6 -> 0), one connected"
echo "# component, zero orphans. The spider is done."
echo
frontier examples/adventureworks/step3-closed "$out/step3-graph.json" "$out/step3-frontier.json"

echo
echo "############################################################"
echo "# SUMMARY: the loop never changed. seed -> extract -> read frontier -> generate ->"
echo "# (run on your server) -> absorb -> extract. The frontier shrank 7 -> 6 -> 0."
echo "# At any step, the union of all the .sql you've gathered IS your current full DDL."
echo "#"
echo "# On the FULL AdventureWorks script a few objects stay genuinely standalone (app-only"
echo "# ufnGet*StatusText functions, awbuildversion, the bulk-loaded transactionhistoryarchive)"
echo "# - referenced nowhere, reported with a reason, not an error. See README.md for that part."
echo "#"
echo "# Artifacts left in examples/adventureworks/_demo-out/ (the emitted queries + per-step graphs)."
echo "############################################################"
