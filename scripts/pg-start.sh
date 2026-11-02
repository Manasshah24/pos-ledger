#!/usr/bin/env bash
# Starts the portable Postgres (tools/pgsql, data in tools/pgdata) on port 54329. No admin needed.
# First time: unzip the EDB binaries into tools/ and run initdb (see README). Then:
#   export LEDGER_TEST_PG="Host=localhost;Port=54329;Username=postgres;Password=postgres;Database=postgres"
cd "$(dirname "$0")/.."
tools/pgsql/bin/pg_ctl.exe -D tools/pgdata -o "-p 54329" -l tools/pg.log -s start < /dev/null > /dev/null 2>&1 &
