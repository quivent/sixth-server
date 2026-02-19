\ lib/db-json.fs -- Database-to-JSON bridge for Sixth Server
\ Field descriptor DSL + generic query-to-JSON handlers.
\ Uses the driver contract (db-exec, db-open, db-row?, db-close, db-path).
\
\ Requires (caller must require before this file):
\   lib/core.fs        (str-reset, str+, str-char, parse-pipe, row-int, n>str)
\   lib/http.fs        (http-200-chunked, http-end-chunked, chunk-check)
\   lib/json.fs        (json-begin, json-open-obj, json-close-obj, etc.)
\   A database driver   (drivers/sqlite.fs or drivers/sixthdb-cli.fs or drivers/sixthdb.fs)

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
\ Filled by +field before calling db-json-array.
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
\ db-json-array ( fd sql-a sql-u -- )
\
\ Chunked JSON array of objects from a database query.
\ Uses db-path (from driver) for the database.
\ Uses field-tbl (from +field calls) for field descriptors.
\
\ Output: [{field1:val, field2:val, ...}, ...]
\ ============================================================

: db-json-array ( fd sql-a sql-u -- )
  >r >r
  http-200-chunked
  str-reset json-begin
  json-open-arr
  db-path r> r>
  db-exec db-open
  begin db-row? while
    dup 0> if
      json-open-obj
      emit-row-json
      json-close-obj
      chunk-check
    else
      2drop
    then
  repeat 2drop
  db-close
  json-close-arr
  http-end-chunked ;

\ ============================================================
\ db-json-strings ( fd sql-a sql-u -- )
\
\ Chunked JSON array of bare strings from a single-column query.
\ Uses db-path for the database.
\
\ Output: ["val1", "val2", ...]
\ ============================================================

: db-json-strings ( fd sql-a sql-u -- )
  >r >r
  http-200-chunked
  str-reset json-begin
  json-open-arr
  db-path r> r>
  db-exec db-open
  begin db-row? while
    dup 0> if json-str chunk-check else 2drop then
  repeat 2drop
  db-close
  json-close-arr
  http-end-chunked ;

\ ============================================================
\ Initialization
\ ============================================================

: db-json-init ( -- ) 0 field-count ! ;

\ Backward compatibility aliases
: sql-json-array ( fd sql-a sql-u -- ) db-json-array ;
: sql-json-strings ( fd sql-a sql-u -- ) db-json-strings ;
