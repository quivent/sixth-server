---
layout: default
title: Architecture
nav_order: 3
---

# Architecture

## Layer Diagram

The framework is layered so you only link what you need:

```
┌─────────────────────────────────────────────────────────┐
│  Layer 2: db-json.fs                                    │
│  Field DSL, db-json-array, db-json-strings              │
│  Depends on: a driver + core + http + json              │
├─────────────────────────────────────────────────────────┤
│  Layer 1: tcp.fs  http.fs  json.fs  server.fs           │
│  TCP sockets, HTTP parse/respond, JSON gen, routing     │
│  Depends on: core (http, json, server)                  │
├─────────────────────────────────────────────────────────┤
│  Layer 0: core.fs                                       │
│  String buffers, numbers, row parsing, str=             │
│  Zero dependencies                                      │
├─────────────────────────────────────────────────────────┤
│  Drivers: sqlite.fs | sixthdb-cli.fs | sixthdb.fs       │
│  Implement the driver contract (db-exec, db-row?, etc.) │
│  Depends on: core                                       │
└─────────────────────────────────────────────────────────┘
```

| File | Layer | Purpose | Dependencies |
|------|-------|---------|--------------|
| `lib/core.fs` | 0 | String buffers, number conversion, row parsing, `str=` | None |
| `lib/tcp.fs` | 1 | TCP socket primitives (accept, read, write, close) | Compiler primitives only |
| `lib/http.fs` | 1 | HTTP request parsing, response helpers, chunked encoding | core |
| `lib/json.fs` | 1 | JSON generation with 8-level nesting stack | core |
| `lib/server.fs` | 1 | Route table (max 64 routes), dispatch, server lifecycle | core, tcp, http |
| `lib/db-json.fs` | 2 | Field DSL, `db-json-array`, `db-json-strings` | core, http, json, a driver |
| `drivers/sqlite.fs` | Driver | SQLite via `sqlite3` subprocess | core |
| `drivers/sixthdb-cli.fs` | Driver | SixthDB via `sixthdb` subprocess | core |
| `drivers/sixthdb.fs` | Driver | SixthDB linked directly (mmap) | core, SixthDB modules |

## Data Flow

A request flows through the framework in a straight line:

```
TCP accept
  → http-read-request (parse method + path from first line)
  → OPTIONS?
      yes → http-options (CORS preflight response)
      no  → route-dispatch
              → scan route table for exact path match
              → match found → execute handler ( fd -- )
              → no match, path is "/" and index set → serve index file
              → no match → try static file from filesystem
              → still no match → http-404
  → close-tcp
  → loop
```

### Handler Execution

Every handler receives a single argument: the TCP socket file descriptor (`fd`). What happens next depends on the endpoint pattern:

**DSL endpoint** (via `db-json-array`):
```
handler called with fd
  → field-reset + +field calls (declare columns)
  → db-json-array:
      → http-200-chunked (send headers, store fd for chunking)
      → db-exec (run SQL query via driver)
      → db-open (prepare result iteration)
      → loop: db-row? → json-open-obj → emit-row-json → json-close-obj → chunk-check
      → json-close-arr → http-end-chunked (send terminating chunk)
```

**Custom endpoint** (via `http-200`):
```
handler called with fd
  → build JSON in str-buf using json-* words
  → http-200 (send headers + body from str-buf in one shot)
```

## Buffer Architecture

The framework uses four statically allocated buffers, each with a distinct role:

| Buffer | Size | Purpose | Words |
|--------|------|---------|-------|
| `str-buf` | 256KB | Primary output buffer. JSON bodies are built here. | `str-reset`, `str+`, `str$`, `str-char` |
| `str2-buf` | 4KB | Secondary buffer for nested operations (dynamic SQL, SixthDB row formatting) | `str2-reset`, `str2+`, `str2$` |
| `resp-buf` | 16KB | HTTP response headers. Built separately so headers can be sent before the body. | `resp-reset`, `resp+`, `resp$`, `resp-char` |
| `cmd-buf` | 4KB | Shell command construction (SQLite/SixthDB subprocess invocations) | `cmd-reset`, `cmd+`, `cmd$`, `cmd-char` |
| `http-buf` | 8KB | HTTP request read buffer. Raw bytes from the socket. | (internal to `http-read-request`) |

### Buffer Lifecycle

On each request:

1. `http-read-request` fills `http-buf` from the socket and parses method/path
2. The handler builds JSON in `str-buf` (via `json-*` words or `db-json-array`)
3. HTTP headers are assembled in `resp-buf`
4. For subprocess drivers, `cmd-buf` holds the shell command
5. For chunked responses, `str-buf` auto-flushes at 200KB via `chunk-check`

## Route Table

The route table holds up to 64 entries. Each entry stores a path string and an execution token (`xt`) for the handler word.

- Routes are registered with `add-route ( path-addr path-u xt -- )`
- Path strings are copied into a 2KB string pool so addresses remain stable
- Dispatch is a linear scan using `str=` for exact path matching
- Handler signature is always `( fd -- )`

## Single-Threaded Model

The server is single-threaded with a blocking accept loop. Each request is fully handled before the next is accepted. This is simple and correct for dashboard/API use cases. SSE connections send an initial event and close immediately -- the frontend reconnects periodically.
