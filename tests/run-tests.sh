#!/usr/bin/env bash
# sql-spider end-to-end regression tests.
#
# Each test drives the REAL CLI (dotnet run) against a small fixture corpus and asserts on the
# observable outputs: node/edge counts, frontier contents, closure verdict, exit codes, and the
# absorb round-trip. Most fixtures encode a specific bug that has been fixed; if one of these
# fails, that bug is back.
#
# usage:  bash tests/run-tests.sh        (from the repo root or anywhere)
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN() { dotnet run -c Release --project "$ROOT" -- "$@"; }
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  ok    - $1"; }
fail() { FAIL=$((FAIL+1)); echo "  FAIL  - $1"; }
check() { # check <description> <haystack> <needle>
  case "$2" in *"$3"*) ok "$1";; *) fail "$1 (wanted: $3)";; esac
}

echo "== build once =="
dotnet build "$ROOT" -c Release -v q -nologo >/dev/null || { echo "BUILD FAILED"; exit 1; }

echo "== shipped examples close with the documented numbers =="
out=$(RUN extract "$ROOT/examples/northwind" "$WORK/nw.json" "$WORK/nwf.json" 2>&1)
check "northwind: 36 nodes / 66 edges" "$out" "nodes:36 edges:66"
check "northwind: closure holds" "$out" "OK: single connected component"
out=$(RUN extract "$ROOT/examples/sqlite" "$WORK/ch.json" "$WORK/chf.json" --dialect sqlite 2>&1)
check "chinook (sqlite): 11 nodes / 10 edges" "$out" "nodes:11 edges:10"
check "chinook: closure holds" "$out" "OK: single connected component"
out=$(RUN extract "$ROOT/examples/adventureworks/step3-closed" "$WORK/aw.json" "$WORK/awf.json" 2>&1)
check "adventureworks step3: closure holds" "$out" "OK: single connected component"
check "adventureworks step3: frontier empty" "$out" "FRONTIER: 0 undefined procs, 0 undefined tables"

echo "== t-sql extraction =="
mkdir -p "$WORK/coa"
printf 'create table dbo.t (id int);\ngo\ncreate or alter procedure dbo.p as select * from dbo.t;\n' > "$WORK/coa/x.sql"
out=$(RUN extract "$WORK/coa" 2>&1)
check "create-or-alter objects register (2 nodes, 1 edge)" "$out" "nodes:2 edges:1"

mkdir -p "$WORK/fn"
printf 'create procedure dbo.p as select dbo.fn_missing(1);\n' > "$WORK/fn/p.sql"
out=$(RUN extract "$WORK/fn" "$WORK/fn/g.json" "$WORK/fn/f.json" 2>&1)
check "undefined function reaches the frontier" "$out" "FRONTIER: 1 undefined procs"
check "frontier names the function" "$(cat "$WORK/fn/f.json")" "fn_missing"

mkdir -p "$WORK/trg"
printf 'create table dbo.orders (id int);\ngo\ncreate table dbo.audit_log (id int);\ngo\ncreate trigger trg on dbo.orders after insert as insert into dbo.audit_log select 1;\n' > "$WORK/trg/x.sql"
out=$(RUN extract "$WORK/trg" 2>&1)
check "trigger connects to its ON table (closed, 3 edges)" "$out" "nodes:3 edges:3"
check "trigger corpus closes" "$out" "OK: single connected component"

mkdir -p "$WORK/wr"
printf 'create procedure dbo.p as begin select id into dbo.archive_new from dbo.src; truncate table dbo.archive; merge dbo.dim as d using dbo.stg as s on d.id=s.id when matched then update set d.x=s.x; end\n' > "$WORK/wr/p.sql"
out=$(RUN extract "$WORK/wr" "$WORK/wr/g.json" "$WORK/wr/f.json" 2>&1)
check "select-into / truncate / merge targets captured (5 frontier tables)" "$out" "5 undefined tables"

mkdir -p "$WORK/insexec"
printf 'create procedure dbo.p as begin insert into #t_rates exec dbo.get_rates @a, @b; end\n' > "$WORK/insexec/p.sql"
out=$(RUN extract "$WORK/insexec" "$WORK/insexec/g.json" "$WORK/insexec/f.json" 2>&1)
check "INSERT...EXEC emits a calls edge (frontier has the proc)" "$out" "FRONTIER: 1 undefined procs"
check "frontier names the exec'd proc" "$(cat "$WORK/insexec/f.json")" "get_rates"

mkdir -p "$WORK/meth"
printf 'create table dbo.emp (node hierarchyid);\ngo\ncreate procedure dbo.p as select node.GetLevel() from dbo.emp;\n' > "$WORK/meth/x.sql"
out=$(RUN extract "$WORK/meth" "$WORK/meth/g.json" "$WORK/meth/f.json" 2>&1)
check "hierarchyid method is not a phantom function" "$out" "FRONTIER: 0 undefined procs"

echo "== sqlite extraction =="
mkdir -p "$WORK/strg"
printf 'CREATE TABLE c (id INTEGER PRIMARY KEY, x TEXT);\nCREATE TABLE log_a (id INTEGER);\nCREATE TRIGGER trg_c BEFORE UPDATE ON c BEGIN INSERT INTO log_a VALUES (1); END;\n' > "$WORK/strg/s.sql"
out=$(RUN extract "$WORK/strg" --dialect sqlite 2>&1)
check "UPDATE trigger: no phantom 'on' table" "$out" "FRONTIER: 0 undefined procs, 0 undefined tables"
check "UPDATE trigger corpus closes" "$out" "OK: single connected component"

mkdir -p "$WORK/strg2"
printf 'CREATE TABLE t (id INTEGER);\nCREATE TABLE log_a (id INTEGER);\nCREATE TABLE log_b (id INTEGER);\nCREATE TRIGGER trg_t AFTER INSERT ON t BEGIN INSERT INTO log_a VALUES (1); INSERT INTO log_b VALUES (2); END;\n' > "$WORK/strg2/s.sql"
out=$(RUN extract "$WORK/strg2" --dialect sqlite 2>&1)
check "multi-statement trigger keeps every edge (closed, 4 nodes)" "$out" "nodes:4 edges:3"
check "multi-statement trigger corpus closes" "$out" "OK: single connected component"

echo "== absorb round-trip =="
mkdir -p "$WORK/ab/corpus" "$WORK/ab/stage"
printf 'create table dbo.orderhdr (id int, custid int);\n' > "$WORK/ab/corpus/orderhdr.sql"
printf 'table_name,column_name,ordinal,data_type,max_length,is_nullable\ncustomer,id,1,int,,NO\ncustomer,note,2,nvarchar,-1,YES\ncustomer,order,3,varchar,10,YES\n' > "$WORK/ab/stage/schema.csv"
printf 'fk_table,fk_column,pk_table,pk_column\norderhdr,custid,customer,id\n' > "$WORK/ab/stage/fk.csv"
RUN absorb "$WORK/ab/stage/schema.csv" "$WORK/ab/stage/fk.csv" --corpus "$WORK/ab/corpus" >/dev/null 2>&1
synth=$(cat "$WORK/ab/corpus/customer.sql" 2>/dev/null || echo MISSING)
check "varchar(max) synthesized (not -1)" "$synth" "nvarchar(max)"
check "reserved-word column bracketed" "$synth" "[order]"
check "fk materialized as alter-table DDL" "$(cat "$WORK/ab/corpus/"*.sql)" "add foreign key"
out=$(RUN extract "$WORK/ab/corpus" 2>&1)
check "fk round-trip closes the ring" "$out" "OK: single connected component"
check "fk round-trip leaves no frontier" "$out" "FRONTIER: 0 undefined procs, 0 undefined tables"

echo "== bucket-1 hardening =="
mkdir -p "$WORK/glob/corpus" "$WORK/glob/stage"
printf 'object_name,object_type,d00\nmyproc,P,create procedure myproc as select 1\n' > "$WORK/glob/stage/defs.csv"
RUN absorb "$WORK/glob/stage/*.csv" --corpus "$WORK/glob/corpus" >/dev/null 2>&1   # QUOTED glob = literal arg, like windows
[ -f "$WORK/glob/corpus/myproc.sql" ] && ok "literal glob arg expands in-process (windows path)" || fail "literal glob arg expands in-process"
RUN absorb "$WORK/glob/stage/*.nope" --corpus "$WORK/glob/corpus" >/dev/null 2>&1; rc=$?
[ $rc -ne 0 ] && ok "zero-absorb exits nonzero" || fail "zero-absorb exits nonzero (got $rc)"
mkdir -p "$WORK/gfeq"
printf 'create table t (id int);\n' > "$WORK/gfeq/t.sql"
RUN extract "$WORK/gfeq" "$WORK/gfeq/g.json" "$WORK/gfeq/f.json" --graphify=$WORK/gfeq/gf.json >/dev/null 2>&1
[ -f "$WORK/gfeq/gf.json" ] && [ -f "$WORK/gfeq/g.json" ] && ok "--graphify=path writes both files, eats no positionals" || fail "--graphify=path form"

echo "== guards and exit codes =="
mkdir -p "$WORK/empty"
out=$(RUN extract "$WORK/empty" 2>&1); rc=$?
check "empty corpus errors loudly" "$out" "no .sql files"
[ $rc -ne 0 ] && ok "empty corpus exits nonzero" || fail "empty corpus exits nonzero"
mkdir -p "$WORK/disc"
printf 'create table a (id int);\n' > "$WORK/disc/a.sql"
printf 'create table b (id int);\n' > "$WORK/disc/b.sql"
RUN round "$WORK/disc" >/dev/null 2>&1; rc=$?
[ $rc -eq 2 ] && ok "round surfaces the audit verdict on the final round (exit 2)" || fail "round surfaces the audit verdict on the final round (got exit $rc)"

echo "== reverse =="
mkdir -p "$WORK/rev"
printf 'referencing,referenced\nsomeproc,myroot     \n' > "$WORK/rev/refs.csv"
out=$(RUN reverse "$WORK/rev/refs.csv" "$WORK/rev/out" --roots myroot 2>&1)
check "right-padded referenced values still match roots" "$out" "1 objects referencing myroot"

echo
echo "== results: $PASS passed, $FAIL failed =="
[ $FAIL -eq 0 ] || exit 1
