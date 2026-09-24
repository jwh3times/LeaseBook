#!/bin/sh
set -eu

# This script must never print the connection string: it contains the schema-owner credential.
connection_string=${ConnectionStrings__Migrations:-}
if [ -z "$connection_string" ]; then
  echo "::error::ConnectionStrings__Migrations is not set; refusing to start the migrator." >&2
  exit 1
fi

modes=$(printf '%s' "$connection_string" \
  | tr ';' '\n' \
  | grep -iE '^[[:space:]]*(ssl ?mode)[[:space:]]*=' \
  | cut -d= -f2- || true)

if [ -z "$modes" ]; then
  echo "::error::The migrations connection sets no SSL Mode. Npgsql defaults to Prefer, which does not verify the server certificate. Add 'SSL Mode=VerifyFull'." >&2
  exit 1
fi

mode_count=$(printf '%s\n' "$modes" | awk 'NF { count++ } END { print count + 0 }')
if [ "$mode_count" -ne 1 ]; then
  echo "::error::The migrations connection sets SSL Mode more than once. Keep one 'SSL Mode=VerifyFull' setting so the effective mode is unambiguous." >&2
  exit 1
fi

mode=$(printf '%s' "$modes" | tr -d '[:space:]' | tr '[:upper:]' '[:lower:]')
case "$mode" in
  verify-full|verifyfull|verify-ca|verifyca)
    echo "The migrations connection verifies the PostgreSQL server certificate."
    ;;
  *)
    echo "::error::The migrations connection uses an SSL Mode that does not verify the server certificate. Set 'SSL Mode=VerifyFull'." >&2
    exit 1
    ;;
esac
