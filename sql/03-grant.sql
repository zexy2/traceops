-- Lets the runtime user call the stored procedures in the app schema only.
GRANT EXECUTE ON SCHEMA::[$(Schema)] TO [traceops_app];
GO
