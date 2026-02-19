\ lib/server.fs -- General-purpose HTTP server framework for Sixth
\
\ Provides:
\   1. Route table with linear dispatch
\   2. Field descriptor DSL for SQL-to-JSON endpoints
\   3. Generic SQL-to-JSON-array handler (chunked)
\   4. Generic SQL-to-JSON-strings handler (chunked)
\   5. Server lifecycle (init, listen, serve)
\
\ Requires (caller must require before this file):
\   modules/srm/srm.fs   (str-buf, sql-exec, sql-open, sql-row?, etc.)
\   lib/tcp.fs            (tcp-server-init, accept-tcp, close-tcp)
\   lib/http.fs           (http-read-request, http-200, http-200-chunked, etc.)
\   lib/json.fs           (json-begin, json-open-obj, json-key, etc.)
\
\ Usage example:
\
\   require lib/server.fs
\
\   : handle-items ( fd -- )
\     field-reset
\     F_INT s" id"     +field
\     F_STR s" name"   +field
\     F_DEC2 s" ratio" +field
\     s" SELECT id,name,CAST(ratio*100 AS INTEGER) FROM items ORDER BY id"
\     sql-json-array ;
\
\   : handle-health ( fd -- )
\     >r str-reset json-begin json-open-obj
\     s" status" s" ok" json-key-str
\     json-close-obj r> http-200 ;
\
\   : register-routes ( -- )
\     s" /api/items" ['] handle-items add-route
\     s" /health"    ['] handle-health add-route ;
\
\   : main ( -- )
\     srm-init
\     s" mydb.db" db-path!
\     server-init
\     s" /index.html" set-index
\     register-routes
\     3000 server-start ;
\   main

\ ============================================================
\ Field Descriptor Types
\ ============================================================

0 constant F_STR    \ string: parse-pipe -> json-key + json-str-val
1 constant F_INT    \ integer: row-int -> json-key-num
2 constant F_DEC2   \ decimal*100: row-int -> json-key-decimal2

\ ============================================================
\ Field Descriptor Table (shared, filled per-request)
\
\ Each field entry: 3 cells = [type] [name-addr] [name-len]
\ Filled by +field before calling sql-json-array.
\ ============================================================

16 constant MAX-FIELDS
variable field-count
create field-tbl 384 allot   \ MAX-FIELDS(16) * 3 entries * 8 bytes

: field-reset ( -- ) 0 field-count ! ;

: +field ( type name-addr name-u -- )
  field-count @ MAX-FIELDS >= if 2drop drop exit then
  field-count @ 3 cells * field-tbl + >r
  r@ 2 cells + !
  r@ cell+ !
  r> !
  1 field-count +! ;

: field-type@ ( n -- type )
  3 cells * field-tbl + @ ;

: field-name@ ( n -- addr u )
  3 cells * field-tbl + cell+ dup @ swap cell+ @ ;

\ ============================================================
\ Generic Row Emitter
\
\ Iterates field descriptors, extracts each column from the
\ pipe-delimited row, emits as JSON key-value pairs.
\ Row string is preserved by parse-pipe/row-int across fields.
\ ============================================================

: emit-row-json ( row-a row-u -- )
  field-count @ 0 ?do
    i field-type@ F_STR = if
      i parse-pipe i field-name@ json-key json-str-val
    else i field-type@ F_INT = if
      i row-int i field-name@ rot json-key-num
    else
      i row-int i field-name@ rot json-key-decimal2
    then then
  loop
  2drop ;

\ ============================================================
\ sql-json-array ( fd sql-a sql-u -- )
\
\ Chunked JSON array of objects from a SQL query.
\ Uses db-path (from srm.fs) for the database.
\ Uses field-tbl (from +field calls) for field descriptors.
\
\ Output: [{field1:val, field2:val, ...}, ...]
\ ============================================================

: sql-json-array ( fd sql-a sql-u -- )
  >r >r
  http-200-chunked
  str-reset json-begin
  json-open-arr
  db-path r> r>
  sql-exec sql-open
  begin sql-row? while
    dup 0> if
      json-open-obj
      emit-row-json
      json-close-obj
      chunk-check
    else
      2drop
    then
  repeat 2drop
  sql-close
  json-close-arr
  http-end-chunked ;

\ ============================================================
\ sql-json-strings ( fd sql-a sql-u -- )
\
\ Chunked JSON array of bare strings from a single-column query.
\ Uses db-path for the database.
\
\ Output: ["val1", "val2", ...]
\ ============================================================

: sql-json-strings ( fd sql-a sql-u -- )
  >r >r
  http-200-chunked
  str-reset json-begin
  json-open-arr
  db-path r> r>
  sql-exec sql-open
  begin sql-row? while
    dup 0> if json-str chunk-check else 2drop then
  repeat 2drop
  sql-close
  json-close-arr
  http-end-chunked ;

\ ============================================================
\ Route Table
\
\ Linear scan dispatch. Each entry: 3 cells = [path-addr] [path-u] [xt]
\ Max 64 routes. Handler signature: ( fd -- )
\ ============================================================

64 constant MAX-ROUTES
variable route-count
create route-tbl 1536 allot   \ MAX-ROUTES(64) * 3 entries * 8 bytes

\ String pool: route paths are copied here so addresses are stable
2048 constant RPOOL-SIZE
create route-pool RPOOL-SIZE allot
variable rpool-used

: route-path@ ( n -- addr u )
  3 cells * route-tbl + dup @ swap cell+ @ ;

: route-xt@ ( n -- xt )
  3 cells * route-tbl + 2 cells + @ ;

\ Temporaries for add-route (avoids stack gymnastics)
variable ar-xt
variable ar-len
variable ar-pool

: add-route ( path-addr path-u xt -- )
  route-count @ MAX-ROUTES >= if drop 2drop exit then
  ar-xt !
  dup ar-len !
  route-pool rpool-used @ + ar-pool !
  drop
  ar-pool @ ar-len @ move
  route-count @ 3 cells * route-tbl +
  ar-pool @ over !
  ar-len @ over cell+ !
  ar-xt @ swap 2 cells + !
  ar-len @ rpool-used +!
  1 route-count +! ;

\ ============================================================
\ Index Page (for "/" redirect)
\ ============================================================

create index-path 256 allot
variable index-path-len

: set-index ( addr u -- )
  dup index-path-len !
  index-path swap move ;

\ ============================================================
\ Route Dispatch
\
\ 1. Check route table for exact path match
\ 2. If "/" and index configured, serve index page
\ 3. Try static file from filesystem
\ 4. 404
\ ============================================================

variable dispatch-fd

\ String equality without return stack (safe inside do-loops)
variable str=-n

: str= ( a1 u1 a2 u2 -- flag )
  rot over <> if 2drop drop false exit then
  \ lengths match
  str=-n !
  begin str=-n @ 0> while
    over c@ over c@ <> if 2drop false exit then
    1+ swap 1+ swap
    -1 str=-n +!
  repeat
  2drop true ;

: route-dispatch ( fd -- )
  dispatch-fd !
  get-path
  route-count @ 0 ?do
    2dup i route-path@ str= if
      2drop dispatch-fd @ i route-xt@ execute
      unloop exit
    then
  loop
  index-path-len @ 0> if
    2dup s" /" str= if
      2drop index-path index-path-len @
      dispatch-fd @ http-send-file if exit then
      dispatch-fd @ http-404 exit
    then
  then
  2dup dispatch-fd @ http-send-file if 2drop exit then
  2drop dispatch-fd @ http-404 ;

\ ============================================================
\ Server Lifecycle
\ ============================================================

: server-handle-client ( client-fd -- )
  dup http-read-request
  get-method s" OPTIONS" str= if
    dup http-options
  else
    dup route-dispatch
  then
  close-tcp drop ;

: server-start ( port -- )
  dup ." Sixth Server on port " . cr
  tcp-server-init
  ." Listening..." cr
  begin
    dup accept-tcp
    dup 0> if
      server-handle-client
    else
      drop
    then
  again ;

: server-init ( -- )
  0 route-count !
  0 field-count !
  0 index-path-len !
  0 rpool-used ! ;
