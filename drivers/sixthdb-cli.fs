\ drivers/sixthdb-cli.fs -- SixthDB CLI driver for Sixth Server
\ Executes queries via the sixthdb CLI subprocess.
\ Implements the standard driver contract.
\
\ Requires (caller must require before this file):
\   lib/core.fs   (str buffers, cmd buffer, parse-pipe, s>number?, n>str)
\
\ External dependency: sixthdb binary on PATH
\
\ SixthDB CLI output uses pipe (|) as field separator and newline
\ as row separator. The command pipeline converts to ASCII 31/30
\ via tr so the same parse-pipe logic works across all drivers.

\ ============================================================
\ Database Path
\ ============================================================

create db-path-buf 256 allot
variable db-path-len

: db-path! ( addr u -- ) dup db-path-len ! db-path-buf swap move ;
: db-path ( -- addr u ) db-path-buf db-path-len @ ;

\ ============================================================
\ Temp Files
\ ============================================================

create sdb-output-buf 32 allot
variable sdb-output-len
: sdb-output ( -- addr u ) sdb-output-buf sdb-output-len @ ;

create sdb-error-buf 32 allot
variable sdb-error-len
: sdb-error-path ( -- addr u ) sdb-error-buf sdb-error-len @ ;

\ ============================================================
\ Error Detection
\ ============================================================

variable sdb-err-addr
variable sdb-err-total
variable sdb-ok-flag

: sdb-check-error ( -- )
  sdb-error-path slurp-file
  dup 0> if
    sdb-err-total ! sdb-err-addr !
    false sdb-ok-flag !
  else
    2drop true sdb-ok-flag !
  then ;

\ ============================================================
\ Command Building
\ Builds: sixthdb <db> sql "<query>" | tr '|\n' '\037\036' > output 2> error
\ ============================================================

: sdb-cmd-query ( db$ sql$ -- )
  cmd-reset
  s" sixthdb " cmd+
  2swap cmd+
  s"  sql " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  s"  | tr '|\n' '\037\036' > " cmd+
  sdb-output cmd+
  s"  2>" cmd+ sdb-error-path cmd+ ;

: sdb-cmd-exec ( db$ sql$ -- )
  cmd-reset
  s" sixthdb " cmd+
  2swap cmd+
  s"  sql " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  s"  2>" cmd+ sdb-error-path cmd+ ;

\ ============================================================
\ Execution
\ ============================================================

: sdb-exec ( db$ sql$ -- )
  sdb-cmd-query cmd$ system sdb-check-error ;

: sdb-run ( db$ sql$ -- )
  sdb-cmd-exec cmd$ system sdb-check-error ;

\ ============================================================
\ Slurp-based result reading
\ ============================================================

variable sdb-slurp-addr
variable sdb-slurp-total
variable sdb-slurp-pos

: sdb-open ( -- )
  sdb-output slurp-file sdb-slurp-total ! sdb-slurp-addr !
  0 sdb-slurp-pos ! ;

: sdb-close ( -- ) ;

: sdb-row? ( -- addr u flag )
  sdb-slurp-pos @ sdb-slurp-total @ >= if 0 0 false exit then
  sdb-slurp-addr @ sdb-slurp-pos @ +
  0
  begin
    sdb-slurp-pos @ sdb-slurp-total @ < while
    sdb-slurp-addr @ sdb-slurp-pos @ + c@ 30 = if
      1 sdb-slurp-pos +!
      true exit
    then
    1 sdb-slurp-pos +!
    1+
  repeat
  dup 0> ;

\ ============================================================
\ Config
\ ============================================================

: sixthdb-cli-output-path! ( addr u -- )
  dup sdb-output-len ! sdb-output-buf swap move ;

: sixthdb-cli-error-path! ( addr u -- )
  dup sdb-error-len ! sdb-error-buf swap move ;

\ ============================================================
\ Initialization
\ ============================================================

: sixthdb-cli-init ( -- )
  s" /tmp/sdb-query.txt" sixthdb-cli-output-path!
  s" /tmp/sdb-error.txt" sixthdb-cli-error-path!
  true sdb-ok-flag ! ;

\ ============================================================
\ Driver Contract
\ ============================================================

: db-init    ( -- ) sixthdb-cli-init ;
: db-exec    ( db-a db-u sql-a sql-u -- ) sdb-exec ;
: db-open    ( -- ) sdb-open ;
: db-row?    ( -- addr u flag ) sdb-row? ;
: db-close   ( -- ) sdb-close ;
: db-ok?     ( -- flag ) sdb-ok-flag @ ;
: db-error   ( -- addr u )
  db-ok? if s" " else sdb-err-addr @ sdb-err-total @ then ;
