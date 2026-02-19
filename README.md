# Sixth Server

A general-purpose HTTP/JSON server framework written in Forth, compiled to native ARM64 binaries. No interpreter, no VM, no runtime dependencies. A 30-line Forth file compiles to a ~100KB native executable that serves JSON APIs backed by SQLite.

## Why This Exists

Sixth is a Forth compiler that produces native ARM64 macOS binaries. It was built as the compilation backend for the CK compiler benchmarking project, where it needed a dashboard server to visualize benchmark data.

The original dashboard server was a single 817-line Forth file with 22 endpoints. Every endpoint repeated the same pattern: open a SQL query, iterate rows, emit JSON fields, send the response. The patterns were screaming for abstraction.

This framework extracts the repeating shapes into reusable machinery:

- **Route table** — register `( fd -- )` handler words against URL paths, get linear dispatch for free
- **Field descriptor DSL** — declare typed columns (`F_STR`, `F_INT`, `F_DEC2`), then call `sql-json-array` and the framework handles SQL execution, row parsing, JSON generation, and chunked HTTP streaming
- **Escape hatch** — any word with signature `( fd -- )` works as a handler, so custom endpoints have full access to the JSON, HTTP, and SQL primitives

The result: a typical SQL-backed JSON endpoint is 5-7 lines of Forth. The 22-endpoint dashboard compiles to a 99KB binary that starts instantly and serves JSON over HTTP with zero external dependencies beyond `sqlite3`.

### What It Allows

Write a complete API server in Forth that:

- Compiles in milliseconds to a native binary
- Serves JSON from SQLite with no middleware, no framework overhead, no garbage collector
- Handles chunked transfer encoding for arbitrarily large result sets
- Serves static files with path traversal protection
- Handles CORS preflight automatically

This is useful if you want a fast, minimal API server for dashboards, internal tools, or any application where the data lives in SQLite and the consumer speaks JSON over HTTP.

## Quick Start

```forth
require modules/srm/srm.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs

: handle-users ( fd -- )
  field-reset
  F_INT s" id"   +field
  F_STR s" name" +field
  s" SELECT id,name FROM users ORDER BY id"
  sql-json-array ;

: handle-health ( fd -- )
  >r str-reset json-begin json-open-obj
  s" status" s" ok" json-key-str
  json-close-obj r> http-200 ;

: register-routes ( -- )
  s" /api/users" ['] handle-users add-route
  s" /health"    ['] handle-health add-route ;

: main ( -- )
  srm-init  server-init
  s" my.db" db-path!
  register-routes
  8080 server-start ;
main
```

Build and run:

```sh
./bin/s3 my-server.fs bin/my-server
./bin/my-server
# Sixth Server on port 8080
# curl localhost:8080/health → {"status":"ok"}
# curl localhost:8080/api/users → [{"id":1,"name":"alice"},{"id":2,"name":"bob"}]
```

## Project Structure

```
bin/s3                      Sixth compiler (ARM64 macOS, ~298KB)
lib/
  server.fs                 Route table, field DSL, SQL-to-JSON handlers, server lifecycle
  http.fs                   HTTP request parsing, response helpers, chunked transfer encoding
  json.fs                   JSON generation with 8-level nesting stack
  tcp.fs                    TCP socket primitives (accept, read, write, close)
modules/srm/
  srm.fs                    String buffers, SQL execution via sqlite3, pipe-delimited row parsing
  db.fs                     Database path management
examples/
  dashboard-server.fs       22-endpoint benchmark dashboard (real-world example)
```

## Framework API

### Route Table

```forth
server-init ( -- )                     \ Zero all state (call once at startup)
add-route ( path-addr path-u xt -- )   \ Register a URL path → handler mapping
set-index ( addr u -- )                \ Set index page for "/" requests
server-start ( port -- )               \ Bind port, enter accept loop (does not return)
```

Handler signature is `( fd -- )`. The fd is a TCP socket file descriptor. Any Forth word that consumes an fd works as a handler.

### Field Descriptor DSL

Declare typed columns before calling a generic SQL handler:

```forth
field-reset ( -- )                     \ Clear the field table
+field ( type name-addr name-u -- )    \ Add a column: type + JSON key name
```

Field types:
- `F_STR` — string value (emitted as `"key":"value"`)
- `F_INT` — integer value (emitted as `"key":123`)
- `F_DEC2` — decimal stored as integer * 100 (emitted as `"key":1.23`)

### SQL-to-JSON Handlers

```forth
sql-json-array ( fd sql-a sql-u -- )   \ Emit [{col:val, ...}, ...] from SQL query
sql-json-strings ( fd sql-a sql-u -- ) \ Emit ["val1", "val2", ...] from single-column query
```

Both use chunked transfer encoding with a 200KB auto-flush threshold. No fixed buffer limit on response size.

### Custom Handlers

For endpoints that need custom logic (multi-query, nested JSON, non-SQL data), write a `( fd -- )` handler using the primitives directly:

- **JSON**: `json-begin`, `json-open-obj`, `json-close-obj`, `json-open-arr`, `json-close-arr`, `json-key`, `json-str-val`, `json-key-num`, `json-key-str`, `json-key-decimal2`
- **HTTP**: `http-200`, `http-200-chunked`, `http-end-chunked`, `http-404`, `http-500`, `http-send-file`
- **SQL**: `db-path`, `sql-exec`, `sql-open`, `sql-row?`, `sql-close`, `parse-pipe`, `row-int`

### String Comparison

The framework provides `str=` which is safe to use inside `do`/`loop` constructs (the built-in `compare` is not):

```forth
str= ( a1 u1 a2 u2 -- flag )          \ True if strings are equal
```

## Example: The Dashboard Server

The included `examples/dashboard-server.fs` is a real 22-endpoint API server for a compiler benchmarking dashboard. It demonstrates all endpoint patterns:

**DSL endpoints** (11 endpoints, ~7 lines each):
```forth
: handle-trajectory ( fd -- )
  field-reset
  F_STR  s" milestone" +field
  F_INT  s" geo"       +field
  F_INT  s" rw"        +field
  F_STR  s" status"    +field
  s" SELECT milestone_name,geomean_target,realwork_target,status FROM trajectory_milestones ORDER BY milestone_order"
  sql-json-array ;
```

**String array endpoints** (1 endpoint, 3 lines):
```forth
: handle-landmines ( fd -- )
  s" SELECT rule FROM architectural_constraints ORDER BY constraint_name ASC"
  sql-json-strings ;
```

**Custom endpoints** (10 endpoints): multi-query stats, nested tier-grouped roadmap, keyed distribution objects, SSE events, static ratio bands.

## Compiler Notes

The included `bin/s3` is the Sixth compiler. It reads Forth source and produces native ARM64 Mach-O executables. Key things to know when writing server code:

- **`cells` is broken at interpret time.** Use literal byte counts in `allot`: write `create buf 1536 allot`, not `create buf 64 3 * cells allot`
- **`compare` uses the return stack.** It crashes inside `do`/`loop`. Use `str=` (provided by `lib/server.fs`) instead.
- **`>r`/`r>` cannot cross `do`/`loop` boundaries.** The loop pushes parameters onto the return stack. Use variables to pass data across loop boundaries.
- **Top-level executable code is silently skipped.** All initialization must happen inside a word that is explicitly called (e.g., define `: main ... ; main`).

## Requirements

- macOS on Apple Silicon (ARM64)
- `sqlite3` on PATH (ships with macOS)

## License

MIT
