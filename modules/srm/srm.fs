\ lib/srm.fs — Stack-Relational Mapper for Sixth
\ Maps database rows to stack values using natural Forth composition.
\ Self-contained: compiles with Sixth (s3). No external dependencies.

\ ============================================================
\ Standard Constants (not built into Sixth primitives)
\ ============================================================

-1 constant true
0 constant false

\ ============================================================
\ String Buffer
\ ============================================================

262144 constant str-max
create str-buf str-max allot
variable str-len
variable str-overflow

: str-reset ( -- ) 0 str-len ! false str-overflow ! ;

: str+ ( addr u -- )
  dup str-len @ + str-max <= if
    str-buf str-len @ + swap dup str-len +! move
  else
    2drop true str-overflow !
  then ;

: str$ ( -- addr u ) str-buf str-len @ ;

: str-char ( c -- )
  str-len @ str-max < if
    str-buf str-len @ + c!
    1 str-len +!
  else
    drop true str-overflow !
  then ;

\ ============================================================
\ Second String Buffer (for nested operations)
\ ============================================================

4096 constant str2-max
create str2-buf str2-max allot
variable str2-len
variable str2-overflow

: str2-reset ( -- ) 0 str2-len ! false str2-overflow ! ;

: str2+ ( addr u -- )
  dup str2-len @ + str2-max <= if
    str2-buf str2-len @ + swap dup str2-len +! move
  else
    2drop true str2-overflow !
  then ;

: str2$ ( -- addr u ) str2-buf str2-len @ ;

\ ============================================================
\ Number Conversion (n -> string)
\ ============================================================

: n>str ( n -- addr u ) dup abs 0 <# #s rot sign #> ;

\ ============================================================
\ Number Parsing (string -> n)
\ Sixth lacks s>number?. Implement decimal parser.
\ ( addr u -- addr u false | n 0 true )
\ ============================================================

: s>number? ( addr u -- addr u false | n 0 true )
  dup 0= if false exit then
  2dup
  over c@ [char] - = dup >r
  if 1 /string then
  dup 0= if r> drop 2drop false exit then
  0 >r
  begin dup 0> while
    over c@ [char] 0 -
    dup 0 < over 9 > or if
      drop 2drop r> drop r> drop false exit
    then
    r> 10 * + >r
    1 /string
  repeat
  2drop 2drop
  r> r> if negate then
  0 true ;

\ ============================================================
\ Field Parsing (pipe-delimited rows)
\ ============================================================

: skip-to-delim ( addr u delim -- addr' u' )
  >r
  begin
    dup 0> while
    over c@ r@ = if 1 /string r> drop exit then
    1 /string
  repeat
  r> drop ;

: field-length ( addr u delim -- n )
  0 >r -rot
  begin dup 0> while
    over c@ 3 pick = if 2drop drop r> exit then
    1 /string r> 1+ >r
  repeat
  2drop drop r> ;

: parse-pipe ( addr u n -- addr u field-addr field-u )
  >r 2dup r>
  0 ?do 31 skip-to-delim loop
  2dup 31 field-length >r drop r> ;

\ ============================================================
\ Database Path (permanent buffer)
\ ============================================================

create db-path-buf 256 allot
variable db-path-len

: db-path! ( addr u -- ) dup db-path-len ! db-path-buf swap move ;
: db-path ( -- addr u ) db-path-buf db-path-len @ ;

\ NOTE: top-level executable code is silently skipped by Sixth.
\ All initialization happens in srm-init, called from main.

\ ============================================================
\ SQL Temp Files (permanent buffers)
\ ============================================================

create sql-output-buf 32 allot
variable sql-output-len
: sql-output ( -- addr u ) sql-output-buf sql-output-len @ ;

create sql-count-buf 32 allot
variable sql-count-len
: sql-count-output ( -- addr u ) sql-count-buf sql-count-len @ ;

\ ============================================================
\ Error Detection (stderr capture)
\ ============================================================

create sql-error-buf 32 allot
variable sql-error-len
: sql-error-path ( -- addr u ) sql-error-buf sql-error-len @ ;

variable sql-err-addr
variable sql-err-total
variable sql-ok-flag

: sql-ok? ( -- flag ) sql-ok-flag @ ;

: sql-error ( -- addr u )
  sql-ok? if s" " else sql-err-addr @ sql-err-total @ then ;

: sql-check-error ( -- )
  sql-error-path slurp-file
  dup 0> if
    sql-err-total ! sql-err-addr !
    false sql-ok-flag !
  else
    2drop true sql-ok-flag !
  then ;

\ ============================================================
\ Command Buffer (separate from str-buf to avoid clobbering)
\ ============================================================

4096 constant cmd-max
create cmd-buf cmd-max allot
variable cmd-len

: cmd-reset ( -- ) 0 cmd-len ! ;

: cmd+ ( addr u -- )
  dup cmd-len @ + cmd-max <= if
    cmd-buf cmd-len @ + swap dup cmd-len +! move
  else
    2drop
  then ;

: cmd-char ( c -- )
  cmd-len @ cmd-max < if
    cmd-buf cmd-len @ + c!
    1 cmd-len +!
  else
    drop
  then ;

: cmd$ ( -- addr u ) cmd-buf cmd-len @ ;

\ ============================================================
\ Command Building (double-quote wrapping)
\ ============================================================

: sql-stderr ( -- )
  s"  2>" cmd+ sql-error-path cmd+ ;

: sql-cmd-query ( db$ sql$ -- )
  cmd-reset
  s" sqlite3 -ascii " cmd+
  2swap cmd+
  s"  " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  s"  > " cmd+
  sql-output cmd+
  sql-stderr ;

: sql-cmd-count ( db$ sql$ -- )
  cmd-reset
  s" sqlite3 " cmd+
  2swap cmd+
  s"  " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  s"  > " cmd+
  sql-count-output cmd+
  sql-stderr ;

\ ============================================================
\ Command Building (exec — no output redirect)
\ ============================================================

: sql-cmd-exec ( db$ sql$ -- )
  cmd-reset
  s" sqlite3 " cmd+
  2swap cmd+
  s"  " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  sql-stderr ;

\ ============================================================
\ Query Execution
\ ============================================================

: sql-exec ( db$ sql$ -- )
  sql-cmd-query cmd$ system sql-check-error ;

: sql-run ( db$ sql$ -- )
  sql-cmd-exec cmd$ system sql-check-error ;

\ ============================================================
\ Slurp-based result reading (replaces read-line)
\ Sixth has slurp-file but not read-line.
\ ============================================================

variable sql-slurp-addr
variable sql-slurp-total
variable sql-slurp-pos

: sql-open ( -- )
  sql-output slurp-file sql-slurp-total ! sql-slurp-addr !
  0 sql-slurp-pos ! ;

: sql-close ( -- ) ;

: sql-row? ( -- addr u flag )
  sql-slurp-pos @ sql-slurp-total @ >= if 0 0 false exit then
  sql-slurp-addr @ sql-slurp-pos @ +
  0
  begin
    sql-slurp-pos @ sql-slurp-total @ < while
    sql-slurp-addr @ sql-slurp-pos @ + c@ 30 = if
      1 sql-slurp-pos +!
      true exit
    then
    1 sql-slurp-pos +!
    1+
  repeat
  dup 0> ;

: sql-count ( db$ sql$ -- n )
  sql-cmd-count cmd$ system sql-check-error
  sql-ok? 0= if 0 exit then
  sql-count-output slurp-file
  dup 0> if
    2dup + 1- c@ 10 = if 1- then
  then
  s>number? if drop else 2drop 0 then ;

\ ============================================================
\ High-Level Iteration
\ ============================================================

: sql-each ( db$ sql$ xt -- )
  >r sql-exec sql-open
  begin sql-row? while
    dup 0> if
      r@ execute
    else
      2drop
    then
  repeat 2drop
  sql-close r> drop ;

: sql-dump ( db$ sql$ -- )
  sql-exec sql-open
  begin sql-row? while
    dup 0> if type cr else 2drop then
  repeat 2drop
  sql-close ;

\ ============================================================
\ SRM Convenience Layer
\ ============================================================

: srm-db ( -- addr u ) db-path ;

: srm-exec ( sql$ -- )
  srm-db 2swap sql-run ;

: srm-query ( sql$ -- )
  srm-db 2swap sql-exec ;

: srm-scalar ( sql$ -- n )
  srm-db 2swap sql-count ;

: srm-print ( sql$ -- )
  srm-db 2swap sql-dump ;

: srm-each ( sql$ xt -- )
  >r srm-db 2swap r> sql-each ;

: srm-int ( addr u n -- n )
  parse-pipe s>number? if drop >r 2drop r> else 2drop 2drop 0 then ;

: row-field ( addr u n -- addr u field-addr field-u )
  parse-pipe ;

: row-int ( addr u n -- addr u value )
  parse-pipe s>number? if drop else 2drop 0 then ;

: row-count ( addr u -- n )
  1 >r
  begin dup 0> while
    over c@ 31 = if r> 1+ >r then
    1 /string
  repeat
  2drop r> ;

\ ============================================================
\ Formatted Table Output (two-pass column alignment)
\ ============================================================

16 constant max-cols
create col-widths max-cols cells allot
variable col-count

: col-width@ ( n -- w ) cells col-widths + @ ;
: col-width! ( w n -- ) cells col-widths + ! ;

: col-widths-reset ( -- )
  0 col-count !
  max-cols 0 do 0 i col-width! loop ;

: col-widths-update ( addr u -- )
  0 >r
  begin
    2dup 31 field-length
    dup r@ col-width@ > if r@ col-width! else drop then
    r@ 1+ col-count @ > if r@ 1+ col-count ! then
    31 skip-to-delim
    r> 1+ >r
    dup 0=
  until
  2drop r> drop ;

: emit-pad ( n -- ) 0 ?do space loop ;

: emit-field-padded ( addr u width -- )
  over - >r type r> 0 max emit-pad ;

: emit-row ( addr u -- )
  col-count @ 0 ?do
    2dup i parse-pipe
    i col-width@ emit-field-padded
    i col-count @ 1- < if ."  | " then
  loop
  2drop cr ;

: emit-separator ( -- )
  col-count @ 0 ?do
    i col-width@ 0 ?do [char] - emit loop
    i col-count @ 1- < if ." -+-" then
  loop
  cr ;

variable tbl-db-a   variable tbl-db-u
variable tbl-sql-a  variable tbl-sql-u

: tbl-save ( db$ sql$ -- )
  tbl-sql-u ! tbl-sql-a ! tbl-db-u ! tbl-db-a ! ;

: tbl-db ( -- addr u ) tbl-db-a @ tbl-db-u @ ;
: tbl-sql ( -- addr u ) tbl-sql-a @ tbl-sql-u @ ;

: sql-table ( db$ sql$ -- )
  tbl-save col-widths-reset
  tbl-db tbl-sql sql-exec sql-open
  begin sql-row? while
    dup 0> if col-widths-update else 2drop then
  repeat 2drop sql-close
  tbl-db tbl-sql sql-exec sql-open
  begin sql-row? while
    dup 0> if emit-row else 2drop then
  repeat 2drop sql-close ;

: srm-table ( sql$ -- )
  srm-db 2swap sql-table ;

\ ============================================================
\ Dynamic SQL (build in str2-buf)
\ ============================================================

: sql-exec2 ( db$ -- )
  str2$ sql-exec ;

: sql-dump2 ( db$ -- )
  str2$ sql-dump ;

: sql-count2 ( db$ -- n )
  str2$ sql-count ;

\ ============================================================
\ Table Utilities
\ ============================================================

: sql-tables ( db$ -- )
  s" SELECT name FROM sqlite_master WHERE type='table' ORDER BY name" sql-dump ;

: sql-table-count ( db$ table$ -- n )
  str2-reset
  s" SELECT COUNT(*) FROM " str2+
  str2+
  str2$ sql-count ;

\ ============================================================
\ Initialization (MUST be called from main before any queries)
\ Sixth skips top-level executable code — all init goes here.
\ ============================================================

: srm-init ( -- )
  s" .ck-metrics.db" db-path!
  s" /tmp/srm-query.txt" dup sql-output-len ! sql-output-buf swap move
  s" /tmp/srm-count.txt" dup sql-count-len ! sql-count-buf swap move
  s" /tmp/srm-error.txt" dup sql-error-len ! sql-error-buf swap move
  true sql-ok-flag ! ;
