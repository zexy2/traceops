#!/usr/bin/env bash
# Runs the web app on http://127.0.0.1:5080 with SQL Server storage.
set -euo pipefail
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
export TraceOps__Store=SqlServer
export TraceOps__SqlServer__Schema="m4_6a1c0f2e9b7d4c3a8e5f1b2c3d4e5f60"
export TraceOps__SqlServer__ExpectedDatabase=TraceOpsLabDB
export TraceOps__SqlServer__ExpectedServerName=traceops-sql
export ConnectionStrings__TraceOpsSqlServer="Server=127.0.0.1,14331;Database=TraceOpsLabDB;User Id=traceops_app;Password=$TRACEOPS_APP_PASSWORD;TrustServerCertificate=True"
dotnet run --project src/TraceOps.Web
