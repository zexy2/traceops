#!/usr/bin/env bash
# Starts SQL Server, creates the database and the runtime login, and
# provisions the schema used by the web app in SQL mode.
set -euo pipefail
cd "$(dirname "$0")/.."
[ -f .env ] || { echo "Create .env from .env.example first." >&2; exit 1; }
set -a; . ./.env; set +a

SCHEMA="m4_6a1c0f2e9b7d4c3a8e5f1b2c3d4e5f60"
RUN_ID="6a1c0f2e-9b7d-4c3a-8e5f-1b2c3d4e5f60"

docker compose up -d --wait sqlserver
sqlcmd() { docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b "$@"; }

sqlcmd -i /sql/01-database.sql -v AppPassword="$TRACEOPS_APP_PASSWORD"
if [ "$(sqlcmd -d TraceOpsLabDB -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.procedures WHERE schema_id = SCHEMA_ID('$SCHEMA')")" != "4" ]; then
  sqlcmd -d TraceOpsLabDB -i /sql/02-app-schema.sql -v Schema="$SCHEMA" RunId="$RUN_ID"
  sqlcmd -d TraceOpsLabDB -i /migrations/001_M4.sql -v Schema="$SCHEMA"
fi
sqlcmd -d TraceOpsLabDB -i /sql/03-grant.sql -v Schema="$SCHEMA"
echo "SQL Server ready. App schema: $SCHEMA"
