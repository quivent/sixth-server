---
layout: default
title: Examples
nav_order: 6
---

# Examples

The included `examples/dashboard-server.fs` is a real 22-endpoint API server for a compiler benchmarking dashboard. It demonstrates every endpoint pattern the framework supports.

## Dashboard Server Overview

**Compile and run:**

```sh
./bin/s3 examples/dashboard-server.fs bin/dashboard-server
./bin/dashboard-server
# Sixth Server on port 3844
# Database: .ck-metrics.db
```

The server uses the SQLite driver, registers 22 routes, serves JSON APIs and static files from a `dashboard/` directory, and supports a configurable port via command-line argument.

### Initialization

```forth
: main ( -- )
  sqlite-init
  db-json-init
  server-init
  s" /tmp/dash-query.txt" sqlite-output-path!
  s" /tmp/dash-count.txt" sqlite-count-path!
  s" /tmp/dash-error.txt" sqlite-error-path!
  dashboard-db db-path!
  s" /dashboard.html" set-index
  register-routes
  argc 2 >= if
    1 argv s>number? if drop server-start
    else 2drop DEFAULT-PORT server-start then
  else DEFAULT-PORT server-start then ;
main
```

Key details:
- Custom temp file paths avoid conflicts with other servers
- `set-index` makes `/` serve the dashboard HTML page
- Port can be overridden via command-line: `./bin/dashboard-server 9090`

---

## Endpoint Patterns

### Pattern 1: DSL Endpoint (~7 lines)

The most common pattern. Declare typed fields, provide a SQL query, and `db-json-array` handles everything: execution, row iteration, JSON generation, chunked streaming.

```forth
: handle-trajectory ( fd -- )
  field-reset
  F_STR  s" milestone" +field
  F_INT  s" geo"       +field
  F_INT  s" rw"        +field
  F_STR  s" status"    +field
  s" SELECT milestone_name,geomean_target,realwork_target,status FROM trajectory_milestones ORDER BY milestone_order"
  db-json-array ;
```

**Output:**
```json
[
  {"milestone":"M1","geo":150,"rw":200,"status":"done"},
  {"milestone":"M2","geo":120,"rw":180,"status":"todo"}
]
```

The dashboard server uses this pattern for 11 endpoints. Each is about 7 lines regardless of how many columns the query returns (up to 13 in `handle-design-docs`).

### Pattern 2: String Array Endpoint (3 lines)

For single-column queries that should return a flat JSON array of strings. No field declarations needed.

```forth
: handle-landmines ( fd -- )
  s" SELECT rule FROM architectural_constraints ORDER BY constraint_name ASC"
  db-json-strings ;
```

**Output:**
```json
["Never use cells at interpret time","compare crashes in do/loop"]
```

### Pattern 3: Multi-Query Stats Object

When a single endpoint needs data from multiple queries, use variables to accumulate results, then build the JSON manually.

```forth
variable stats-geo
variable stats-total

: stats-query1 ( -- )
  dashboard-db
  s" SELECT CAST(ROUND(EXP(AVG(LN(ratio)))*100) AS INTEGER) FROM ..."
  db-exec db-open
  db-row? if
    dup 0> if 0 row-int stats-geo ! then 2drop
  else 2drop then
  db-close ;

: handle-stats ( fd -- )
  >r
  0 stats-geo ! 0 stats-total !
  stats-query1
  str-reset json-begin json-open-obj
  stats-geo @ s" geomean" rot json-key-decimal2
  stats-total @ s" total" rot json-key-num
  json-close-obj
  r> http-200 ;
```

The pattern: save `fd` to the return stack (`>r`), zero accumulators, run queries, build JSON in `str-buf`, send with `http-200` which retrieves `fd` from `r>`.

### Pattern 4: Nested/Grouped JSON

For tier-grouped structures where you need to detect group boundaries and nest arrays inside objects.

```forth
create prev-tier 64 allot
variable prev-tier-len

: handle-roadmap ( fd -- )
  http-200-chunked
  str-reset json-begin
  json-open-arr
  0 prev-tier-len !
  dashboard-db
  s" SELECT tier_name,item_name,status,... FROM ... ORDER BY tier,priority"
  db-exec db-open
  begin db-row? while
    dup 0> if
      0 parse-pipe
      2dup prev-tier prev-tier-len @ compare 0= if
        2drop  \ same tier, continue
      else
        prev-tier-len @ 0> if json-close-arr json-close-obj then
        json-open-obj
        2dup s" tier" json-key json-str-val
        s" items" json-key json-key-arr
        dup prev-tier-len ! prev-tier swap move
      then
      json-open-obj
      1 parse-pipe s" name" json-key json-str-val
      2 parse-pipe s" status" json-key json-str-val
      2drop
      json-close-obj
      chunk-check
    else 2drop then
  repeat 2drop
  db-close
  prev-tier-len @ 0> if json-close-arr json-close-obj then
  json-close-arr
  http-end-chunked ;
```

**Output:**
```json
[
  {"tier":"Tier 1","items":[{"name":"opt-a","status":"done"}, ...]},
  {"tier":"Tier 2","items":[{"name":"opt-b","status":"todo"}, ...]}
]
```

### Pattern 5: Keyed Object

When the JSON output should be an object keyed by a column value rather than an array.

```forth
: handle-bench-fixes ( fd -- )
  >r
  str-reset json-begin
  json-open-obj
  dashboard-db
  s" SELECT benchmark_name,fix_category,note FROM bench_fixes ORDER BY benchmark_name"
  db-exec db-open
  begin db-row? while
    dup 0> if
      0 parse-pipe 2dup json-key json-key-obj
      1 parse-pipe s" fix" json-key json-str-val
      2 parse-pipe s" note" json-key json-str-val
      2drop json-close-obj
    else 2drop then
  repeat 2drop
  db-close
  json-close-obj
  r> http-200 ;
```

**Output:**
```json
{
  "fibonacci": {"fix":"loop-opt","note":"unroll inner loop"},
  "sieve":     {"fix":"array-opt","note":"use bit array"}
}
```

### Pattern 6: SSE Events

Single-line handler for Server-Sent Events. The server is single-threaded, so it sends a connected event and closes. The frontend reconnects every few seconds.

```forth
: handle-events ( fd -- ) http-sse ;
```

### Pattern 7: Static Data (No SQL)

For endpoints that return fixed data, build JSON directly with no database involved.

```forth
: handle-ratio-bands ( fd -- )
  >r
  str-reset json-begin
  json-open-arr
  s" <1x"   0 1   emit-band
  s" 1-2x"  1 2   emit-band
  s" 2-5x"  2 5   emit-band
  json-close-arr
  r> http-200 ;
```

---

## Route Registration

All routes are registered in a single word. The convention is to group DSL endpoints and custom endpoints separately:

```forth
: register-routes ( -- )
  \ DSL endpoints
  s" /api/trajectory"        ['] handle-trajectory        add-route
  s" /api/milestones"        ['] handle-milestones        add-route
  s" /api/landmines"         ['] handle-landmines         add-route
  \ ...
  \ Custom endpoints
  s" /api/stats"             ['] handle-stats             add-route
  s" /api/roadmap"           ['] handle-roadmap           add-route
  s" /api/events"            ['] handle-events            add-route
  s" /health"                ['] handle-health            add-route ;
```
