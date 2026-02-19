\ lib/tcp.fs -- TCP socket primitives for CK dashboard server
\
\ Compiler primitives (added to src/stack.fs + src/compile.fs):
\   socket            ( -- fd )
\   reuse-socket-addr ( fd -- ior )
\   bind-port         ( fd port -- ior )
\   listen            ( fd backlog -- ior )
\   accept-tcp        ( fd -- client-fd )
\
\ Existing primitives reused for TCP:
\   close-file  ( fd -- ior )
\   write-file  ( addr u fd -- ior )
\   read-file   ( addr u fd -- u2 ior )

variable server-fd

: close-tcp ( fd -- ior ) close-file ;
: write-tcp ( addr u fd -- ior ) write-file ;
: read-tcp  ( addr u fd -- u2 ior ) read-file ;

: tcp-server-init ( port -- fd )
  >r
  socket
  dup reuse-socket-addr drop
  dup r> bind-port drop
  dup 32 listen drop
  dup server-fd ! ;

: tcp-server-accept ( -- client-fd )
  server-fd @ accept-tcp ;
