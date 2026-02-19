---
layout: default
title: API Reference
nav_order: 4
---

# API Reference

All words are organized by source file.

---

## Route Table (`lib/server.fs`)

```forth
server-init  ( -- )                     \ Zero route table, index path, string pool
add-route    ( path-addr path-u xt -- ) \ Register a URL path → handler mapping
set-index    ( addr u -- )              \ Set path for "/" requests (e.g., "/index.html")
server-start ( port -- )               \ Bind port, enter accept loop (does not return)
```

- Handler signature is `( fd -- )` where `fd` is a TCP socket file descriptor
- Maximum 64 routes, 2KB path string pool
- Dispatch is a linear scan with exact path matching

---

## Field DSL (`lib/db-json.fs`)

```forth
field-reset   ( -- )                    \ Clear the field table (call before +field)
+field        ( type name-addr name-u -- )  \ Add a typed column descriptor
db-json-init  ( -- )                    \ Initialize field state (call once at startup)
```

### Field Types

| Constant | Value | JSON Output | Example |
|----------|-------|-------------|---------|
| `F_STR` | 0 | `"key":"value"` | `F_STR s" name" +field` |
| `F_INT` | 1 | `"key":123` | `F_INT s" id" +field` |
| `F_DEC2` | 2 | `"key":1.23` (integer/100) | `F_DEC2 s" ratio" +field` |

Maximum 16 fields per request.

---

## Database-to-JSON (`lib/db-json.fs`)

```forth
db-json-array   ( fd sql-a sql-u -- )   \ Emit [{col:val, ...}, ...] from query
db-json-strings ( fd sql-a sql-u -- )   \ Emit ["val1", "val2", ...] from single-column query
```

Both words:
- Use the current `db-path` for the database
- Use `field-tbl` (from `+field` calls) for column descriptors (`db-json-array` only)
- Use chunked transfer encoding
- Auto-flush at 200KB via `chunk-check`

---

## JSON Generation (`lib/json.fs`)

### Structure

```forth
json-begin      ( -- )                  \ Initialize nesting state (call before building JSON)
json-open-obj   ( -- )                  \ Emit '{', push nesting level
json-close-obj  ( -- )                  \ Emit '}', pop nesting level
json-open-arr   ( -- )                  \ Emit '[', push nesting level
json-close-arr  ( -- )                  \ Emit ']', pop nesting level
json-key-obj    ( -- )                  \ Emit '{' after a json-key (no separator)
json-key-arr    ( -- )                  \ Emit '[' after a json-key (no separator)
```

Maximum nesting depth: 8 levels.

### Key/Value Pairs

```forth
json-key         ( addr u -- )          \ Emit "key": (with separator if not first)
json-str-val     ( addr u -- )          \ Emit "value" (no separator, use after json-key)
json-key-str     ( k-a k-u v-a v-u -- ) \ Emit "key":"value"
json-key-num     ( addr u n -- )        \ Emit "key":123
json-key-decimal2 ( addr u n -- )       \ Emit "key":1.23 (n/100)
json-key-null    ( addr u -- )          \ Emit "key":null
```

### Bare Values (array elements)

```forth
json-str        ( addr u -- )           \ Emit "value" (with separator)
json-num        ( n -- )                \ Emit 123 (with separator)
json-decimal2   ( n -- )                \ Emit 1.23 (n/100, with separator)
json-null       ( -- )                  \ Emit null
json-true       ( -- )                  \ Emit true
json-false      ( -- )                  \ Emit false
```

### Misc

```forth
json-comma      ( -- )                  \ Emit a raw comma
json-escape+    ( addr u -- )           \ Append string to str-buf with JSON escaping
```

All JSON words write into `str-buf` via `str+`/`str-char`. Separators (commas) are managed automatically by the nesting stack.

---

## HTTP Responses (`lib/http.fs`)

### Standard Responses

```forth
http-200         ( fd -- )              \ Send 200 with JSON body from str-buf
http-404         ( fd -- )              \ Send 404 with {"error":"not found"}
http-500         ( fd -- )              \ Send 500 with {"error":"internal server error"}
http-options     ( fd -- )              \ Send 200 CORS preflight response
http-sse         ( fd -- )              \ Send SSE connected event, then close
```

`http-200` assembles headers in `resp-buf`, then sends headers and `str$` body separately (no copy).

### Chunked Transfer Encoding

```forth
http-200-chunked ( fd -- )              \ Send chunked response headers, store fd
http-chunk-flush ( -- )                 \ Flush str-buf as one HTTP chunk
http-end-chunked ( -- )                 \ Flush remaining data + send terminating chunk
chunk-check      ( -- )                 \ Auto-flush if str-buf exceeds 200KB
```

### Static File Serving

```forth
http-send-file ( path-a path-u fd -- flag )  \ Serve file from "dashboard/" directory
```

Prepends `"dashboard"` to the URL path, detects content type from extension (`.html`, `.js`, `.css`, `.json`), sends with appropriate headers. Returns `true` if the file was found and served. Includes path traversal protection (rejects `..`).

### Request Parsing

```forth
http-read-request ( fd -- )             \ Read request from socket, parse first line
get-method        ( -- addr u )         \ Get parsed HTTP method ("GET", "OPTIONS", etc.)
get-path          ( -- addr u )         \ Get parsed URL path (query string stripped)
```

---

## String Utilities (`lib/core.fs`)

### Primary String Buffer (256KB)

```forth
str-reset  ( -- )                       \ Reset buffer to empty
str+       ( addr u -- )                \ Append string to buffer
str$       ( -- addr u )                \ Get buffer contents
str-char   ( c -- )                     \ Append single character
```

### Secondary String Buffer (4KB)

```forth
str2-reset ( -- )                       \ Reset secondary buffer
str2+      ( addr u -- )                \ Append to secondary buffer
str2$      ( -- addr u )                \ Get secondary buffer contents
```

### Command Buffer (4KB)

```forth
cmd-reset  ( -- )                       \ Reset command buffer
cmd+       ( addr u -- )                \ Append to command buffer
cmd$       ( -- addr u )                \ Get command buffer contents
cmd-char   ( c -- )                     \ Append single character to command buffer
```

### Conversion

```forth
n>str      ( n -- addr u )              \ Convert signed integer to string
s>number?  ( addr u -- addr u false | n 0 true )  \ Parse decimal string to integer
```

### Comparison

```forth
str=       ( a1 u1 a2 u2 -- flag )      \ String equality (safe inside do/loop)
```

Unlike the built-in `compare`, `str=` does not use the return stack and is safe to call inside `do`/`loop` constructs.

---

## Row Parsing (`lib/core.fs`)

```forth
parse-pipe ( addr u n -- addr u field-addr field-u )  \ Extract field n from delimited row
row-field  ( addr u n -- addr u field-addr field-u )  \ Alias for parse-pipe
row-int    ( addr u n -- addr u value )               \ Extract field n as integer
row-count  ( addr u -- n )                            \ Count fields in a row
```

Fields are separated by ASCII 31 (unit separator). The row string (`addr u`) is preserved on the stack across calls, so you can extract multiple fields from the same row.
