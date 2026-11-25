#!/usr/bin/env bash
# Runs the SQL Server integration and host suites against docker-compose SQL.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
export M4_SQLSERVER_FIXTURE_CONNECTION="Server=127.0.0.1,14331;Database=TraceOpsLabDB;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=True"
export ConnectionStrings__TraceOpsSqlServer="Server=127.0.0.1,14331;Database=TraceOpsLabDB;User Id=traceops_app;Password=$TRACEOPS_APP_PASSWORD;TrustServerCertificate=True"
dotnet test tests/TraceOps.SqlServer.Tests -p:RestoreLockedMode=true
dotnet test tests/TraceOps.SqlHost.Tests -p:RestoreLockedMode=true
