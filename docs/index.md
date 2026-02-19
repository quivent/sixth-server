---
layout: default
title: Home
nav_order: 1
---

# Sixth Server

A general-purpose HTTP/JSON server framework written in Forth, compiled to native ARM64 binaries via the bundled Sixth compiler. No interpreter, no VM, no runtime dependencies. A 30-line Forth file compiles to a ~100KB native executable that serves JSON APIs.

---

## Why Sixth Server

- **No runtime** -- compiles to a native ARM64 Mach-O binary. No interpreter, no VM, no shared libraries
- **Tiny binaries** -- a complete API server in ~100KB
- **Layered architecture** -- link only what you need: core, HTTP, JSON, database
- **Database-agnostic** -- swap SQLite, SixthDB CLI, or SixthDB direct-link without changing endpoint code
- **DSL endpoints** -- declare typed fields and a SQL query; the framework handles parsing, JSON generation, and chunked streaming
- **Escape hatch** -- any `( fd -- )` word works as a handler, giving full access to all primitives

## Quick Example

```forth
require lib/core.fs
require drivers/sqlite.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs

: handle-users ( fd -- )
  field-reset
  F_INT s" id"   +field
  F_STR s" name" +field
  s" SELECT id,name FROM users ORDER BY id"
  db-json-array ;

: handle-health ( fd -- )
  >r str-reset json-begin json-open-obj
  s" status" s" ok" json-key-str
  json-close-obj r> http-200 ;

: register-routes ( -- )
  s" /api/users" ['] handle-users add-route
  s" /health"    ['] handle-health add-route ;

: main ( -- )
  sqlite-init  db-json-init  server-init
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
# curl localhost:8080/health        → {"status":"ok"}
# curl localhost:8080/api/users     → [{"id":1,"name":"alice"},{"id":2,"name":"bob"}]
```

## Documentation

| Page | Description |
|------|-------------|
| [Getting Started](getting-started) | Requirements, build commands, usage patterns |
| [Architecture](architecture) | Layers, data flow, buffer system |
| [API Reference](api-reference) | Every framework word with stack effects |
| [Drivers](drivers) | Driver contract, SQLite, SixthDB |
| [Examples](examples) | Walkthrough of the 22-endpoint dashboard server |
| [Compiler Notes](compiler-notes) | Sixth compiler gotchas and workarounds |
