\ modules/srm/srm.fs -- Backward compatibility shim
\ Existing code that requires this file continues to work unchanged.
\ All functionality now lives in lib/core.fs and drivers/sqlite.fs.

require lib/core.fs
require drivers/sqlite.fs
