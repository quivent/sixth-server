\ lib/db.fs — Named queries for CK metrics database
\ Uses SRM (Stack-Relational Mapper) for all database access.

require modules/srm/srm.fs

\ ============================================================
\ Session Queries
\ ============================================================

: session-num ( -- n )
  s" SELECT MAX(session_num) FROM metric_sessions" srm-scalar ;

\ ============================================================
\ Core Metrics
\ ============================================================

: geomean-current ( -- n )
  s" SELECT CAST(geomean_all * 100 AS INTEGER) FROM core_metrics ORDER BY rowid DESC LIMIT 1" srm-scalar ;

: benchmark-pass ( -- n )
  s" SELECT benchmark_pass FROM core_metrics ORDER BY rowid DESC LIMIT 1" srm-scalar ;

: benchmark-total ( -- n )
  s" SELECT benchmark_total FROM core_metrics ORDER BY rowid DESC LIMIT 1" srm-scalar ;

\ ============================================================
\ Session Status (depends on core metrics above)
\ ============================================================

: session-status ( -- )
  ." Session " session-num . cr
  ." Geomean: " geomean-current dup 100 / n>str type [char] . emit 100 mod
  dup 10 < if [char] 0 emit then n>str type ." x" cr
  ." Pass: " benchmark-pass . ." / " benchmark-total . cr ;

\ ============================================================
\ Gap Analysis
\ ============================================================

: optimization-priorities ( -- )
  s" SELECT root_cause, benchmark_count FROM gap_analysis ORDER BY benchmark_count DESC LIMIT 10" srm-print ;

\ ============================================================
\ Tier Status
\ ============================================================

: tier-status ( -- )
  s" SELECT tier_number, tier_name, items_completed, items_total FROM tier_status ORDER BY tier_number" srm-print ;

\ ============================================================
\ Roadmap
\ ============================================================

: roadmap-next ( -- )
  s" SELECT tier, item_number, item_name FROM roadmap_items WHERE status='todo' ORDER BY tier, priority LIMIT 5" srm-print ;

: roadmap-done ( -- )
  s" SELECT tier, item_number, item_name FROM roadmap_items WHERE status='done' ORDER BY tier, item_number" srm-print ;

\ ============================================================
\ Resource Pressure
\ ============================================================

: resource-pressure ( -- )
  s" SELECT * FROM resource_pressure ORDER BY rowid DESC LIMIT 1" srm-print ;

\ ============================================================
\ Constraints & Landmines
\ ============================================================

: constraints-list ( -- )
  s" SELECT constraint_name, rule FROM architectural_constraints ORDER BY constraint_name" srm-print ;

: landmines ( -- )
  s" SELECT landmine_name, rule FROM codebook_landmines" srm-print ;

\ ============================================================
\ Fusion Opportunities
\ ============================================================

: fusion-opportunities ( -- )
  s" SELECT pattern_name, pattern_tokens, benchmarks_affected FROM instruction_fusion_patterns ORDER BY benchmarks_affected DESC" srm-print ;

\ ============================================================
\ Worklog
\ ============================================================

: worklog-recent ( -- )
  s" SELECT timestamp, title, work_type FROM worklog_entries ORDER BY timestamp DESC LIMIT 10" srm-print ;

\ ============================================================
\ Write Operations
\ ============================================================

: work-log ( title$ type$ -- )
  str2-reset
  s" INSERT INTO worklog_entries(timestamp,title,work_type) VALUES(datetime('now'),'" str2+
  2swap str2+
  s" ','" str2+
  str2+
  s" ')" str2+
  str2$ srm-exec ;

: mark-done ( item$ -- )
  str2-reset
  s" UPDATE roadmap_items SET status='done' WHERE item_name='" str2+
  str2+
  s" '" str2+
  str2$ srm-exec ;

\ ============================================================
\ Generic Query Interface
\ ============================================================

: db-query ( sql$ -- ) srm-print ;
: db-exec ( sql$ -- ) srm-exec ;
: db-count ( sql$ -- n ) srm-scalar ;
