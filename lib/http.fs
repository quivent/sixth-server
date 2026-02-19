\ lib/http.fs -- HTTP request parser + response helpers
\
\ Requires (caller must require before this file):
\   lib/core.fs   (str-reset, str+, str$, str-char, str-len, n>str)
\
\ Uses compiler primitives:
\   read-file  ( addr u fd -- u2 ior )
\   write-file ( addr u fd -- ior )

\ ============================================================
\ Request Read Buffer
\ ============================================================

8192 constant HTTP-BUF-SIZE
create http-buf HTTP-BUF-SIZE allot
variable http-len

\ ============================================================
\ Response Buffer
\ ============================================================

16384 constant RESP-BUF-SIZE
create resp-buf RESP-BUF-SIZE allot
variable resp-len

: resp-reset ( -- ) 0 resp-len ! ;

: resp+ ( addr u -- )
  dup resp-len @ + RESP-BUF-SIZE <= if
    resp-buf resp-len @ + swap dup resp-len +! move
  else
    2drop
  then ;

: resp$ ( -- addr u ) resp-buf resp-len @ ;

: resp-char ( c -- )
  resp-len @ RESP-BUF-SIZE < if
    resp-buf resp-len @ + c!
    1 resp-len +!
  else
    drop
  then ;

\ ============================================================
\ HTTP Response Helpers
\ ============================================================

: http-crlf ( -- ) 13 resp-char 10 resp-char ;

: http-200-line ( -- )
  s" HTTP/1.1 200 OK" resp+ http-crlf ;

: http-404-line ( -- )
  s" HTTP/1.1 404 Not Found" resp+ http-crlf ;

: http-json-ct ( -- )
  s" Content-Type: application/json" resp+ http-crlf ;

: http-ct ( addr u -- )
  s" Content-Type: " resp+ resp+ http-crlf ;

\ Check if string ends with suffix
: ends-with? ( addr u suf-addr suf-u -- flag )
  2over nip over < if 2drop 2drop false exit then
  swap >r >r
  r@ - + r@
  r> r> swap compare 0= ;

\ Detect content type from path
: path-content-type ( addr u -- ct-addr ct-u )
  2dup s" .html" ends-with? if 2drop s" text/html; charset=utf-8" exit then
  2dup s" .js"   ends-with? if 2drop s" application/javascript" exit then
  2dup s" .css"  ends-with? if 2drop s" text/css" exit then
  2dup s" .json" ends-with? if 2drop s" application/json" exit then
  2drop s" application/octet-stream" ;

: http-cors-headers ( -- )
  s" Access-Control-Allow-Origin: *" resp+ http-crlf
  s" Access-Control-Allow-Methods: GET, OPTIONS" resp+ http-crlf
  s" Access-Control-Allow-Headers: Content-Type" resp+ http-crlf ;

: http-content-length ( n -- )
  s" Content-Length: " resp+ n>str resp+ http-crlf ;

: http-connection-close ( -- )
  s" Connection: close" resp+ http-crlf ;

\ ============================================================
\ Request Parsing
\ Parse first line: "GET /path HTTP/1.1\r\n..."
\ ============================================================

create req-method 16 allot
variable req-method-len
create req-path 256 allot
variable req-path-len

\ Find length of token (chars until space or end)
: token-len ( addr u -- n )
  0 >r
  begin dup 0> while
    over c@ 32 = if 2drop r> exit then
    1 /string r> 1+ >r
  repeat
  2drop r> ;

\ Skip leading spaces
: skip-spaces ( addr u -- addr' u' )
  begin dup 0> if over c@ 32 = else 0 then while
    1 /string
  repeat ;

\ Parse: "GET /path HTTP/1.1"
\ Extracts method and path into static buffers.
: parse-request-line ( addr u -- )
  skip-spaces
  2dup token-len >r
  r@ 15 min req-method-len !
  over req-method r@ 15 min move
  r> /string
  skip-spaces
  2dup token-len >r
  r@ 255 min req-path-len !
  over req-path r> 255 min move
  2drop
  \ Strip query string: truncate path at first '?'
  req-path-len @ 0 do
    req-path i + c@ [char] ? = if i req-path-len ! leave then
  loop ;

\ Read HTTP request from fd, parse first line
: http-read-request ( fd -- )
  >r
  http-buf HTTP-BUF-SIZE r> read-file
  drop  \ drop ior
  http-len !
  \ Find end of first line (CR or LF), then parse just that
  http-buf http-len @
  \ Scan for CR or LF to find line end
  2dup  \ save for parse
  begin dup 0> while
    over c@ dup 13 = swap 10 = or if
      \ Found end of line. Calculate line length.
      drop nip over - \ ( orig-addr line-len )
      parse-request-line exit
    then
    1 /string
  repeat
  \ No CR/LF found, parse entire buffer as one line
  2drop parse-request-line ;

: get-method ( -- addr u ) req-method req-method-len @ ;
: get-path ( -- addr u ) req-path req-path-len @ ;

\ ============================================================
\ Response Sending
\ ============================================================

\ Send resp-buf contents to fd
: http-send-resp ( fd -- )
  >r resp$ r> write-file drop ;

\ Build and send 200 JSON response.
\ Body must already be in str$ (SRM string buffer).
\ Headers sent from resp-buf, body sent directly from str$ — no copy.
: http-200 ( fd -- )
  >r
  resp-reset
  http-200-line
  http-json-ct
  http-cors-headers
  http-connection-close
  str$ nip http-content-length
  http-crlf
  resp$ r@ write-file drop
  str$ r> write-file drop ;

\ ============================================================
\ Chunked Transfer Encoding
\ Stream JSON to socket in chunks — no fixed buffer limit.
\ ============================================================

variable chunk-fd

create hex-out 16 allot

: n>hex ( n -- addr u )
  dup 0<= if drop hex-out 15 + [char] 0 over c! 1 exit then
  hex-out 16 + 0 >r
  begin
    1- over 15 and
    dup 10 < if [char] 0 + else 10 - [char] a + then
    over c!
    swap 4 rshift swap
    r> 1+ >r
    over 0=
  until
  nip r> ;

create crlf-pair 4 allot
: write-crlf ( fd -- )
  13 crlf-pair c! 10 crlf-pair 1+ c!
  crlf-pair 2 rot write-file drop ;

\ Flush str-buf contents as one HTTP chunk to chunk-fd
: http-chunk-flush ( -- )
  str-len @ 0= if exit then
  chunk-fd @ 0= if exit then
  str-len @ n>hex chunk-fd @ write-file drop
  chunk-fd @ write-crlf
  str$ chunk-fd @ write-file drop
  chunk-fd @ write-crlf
  str-reset ;

\ Send chunked response headers (no Content-Length needed)
: http-200-chunked ( fd -- )
  >r
  resp-reset
  http-200-line
  http-json-ct
  http-cors-headers
  http-connection-close
  s" Transfer-Encoding: chunked" resp+ http-crlf
  http-crlf
  resp$ r@ write-file drop
  r> chunk-fd ! ;

\ End chunked response: flush remaining data + terminating chunk
: http-end-chunked ( -- )
  http-chunk-flush
  s" 0" chunk-fd @ write-file drop
  chunk-fd @ write-crlf
  chunk-fd @ write-crlf
  0 chunk-fd ! ;

\ Call after each row to auto-flush if buffer getting full (200KB threshold)
: chunk-check ( -- )
  chunk-fd @ 0= if exit then
  str-len @ 204800 > if http-chunk-flush then ;

\ Send 404 response
: http-404 ( fd -- )
  >r
  str-reset
  [char] { str-char
  [char] " str-char s" error" str+ [char] " str-char
  [char] : str-char
  [char] " str-char s" not found" str+ [char] " str-char
  [char] } str-char
  resp-reset
  http-404-line
  http-json-ct
  http-cors-headers
  http-connection-close
  str$ nip http-content-length
  http-crlf
  str$ resp+
  r> http-send-resp ;

\ Send 500 error response
: http-500 ( fd -- )
  >r
  str-reset
  [char] { str-char
  [char] " str-char s" error" str+ [char] " str-char
  [char] : str-char
  [char] " str-char s" internal server error" str+ [char] " str-char
  [char] } str-char
  resp-reset
  s" HTTP/1.1 500 Internal Server Error" resp+ http-crlf
  http-json-ct
  http-cors-headers
  http-connection-close
  str$ nip http-content-length
  http-crlf
  str$ resp+
  r> http-send-resp ;

\ Send SSE response: connected event, then close
\ Single-threaded server can't hold connections open, so we send the
\ initial event and close. Frontend reconnects every 5s gracefully.
: http-sse ( fd -- )
  >r
  resp-reset
  http-200-line
  s" Content-Type: text/event-stream" resp+ http-crlf
  s" Cache-Control: no-cache" resp+ http-crlf
  http-cors-headers
  http-connection-close
  http-crlf
  s" event: connected" resp+ http-crlf
  s" data: {}" resp+ http-crlf
  http-crlf
  r> http-send-resp ;

\ Send OPTIONS response (CORS preflight)
: http-options ( fd -- )
  >r
  resp-reset
  http-200-line
  http-cors-headers
  http-connection-close
  0 http-content-length
  http-crlf
  r> http-send-resp ;

\ Static file serving
create static-path-buf 512 allot
variable static-fd

\ Check path has no ".." components (path traversal guard)
: path-safe? ( addr u -- flag )
  begin dup 1 > while
    over c@ [char] . = if
      over 1+ c@ [char] . = if
        dup 2 = if 2drop false exit then
        over 2 + c@ [char] / = if 2drop false exit then
      then
    then
    1 /string
  repeat
  2drop true ;

\ Send a static file: prepend "dashboard" to url-path, slurp, send
\ ( path-addr path-u fd -- flag ) flag=true if served
: http-send-file ( path-addr path-u fd -- flag )
  static-fd !
  2dup path-safe? 0= if 2drop false exit then
  \ Build filesystem path: "dashboard" + url-path
  s" dashboard" static-path-buf swap move
  dup 9 + 512 > if 2drop false exit then
  static-path-buf 9 + swap dup >r move r>
  9 + static-path-buf swap
  \ Stack: ( fspath fspath-len )
  2dup path-content-type >r >r  \ save content-type
  slurp-file dup 0= if
    2drop r> r> 2drop false exit
  then
  \ Stack: ( body-addr body-u ) R: ( ct-u ct-addr )
  resp-reset
  http-200-line
  r> r> http-ct
  http-cors-headers
  http-connection-close
  dup http-content-length
  http-crlf
  resp$ static-fd @ write-file drop
  static-fd @ write-file drop
  true ;
