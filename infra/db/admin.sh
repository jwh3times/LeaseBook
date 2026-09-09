#!/usr/bin/env bash
# Never enable tracing: passwords enter only through secret-backed environment variables/stdin.
set +x
set -euo pipefail
export LC_ALL=C
fail() { printf 'dbadmin: %s\n' "$1" >&2; exit 1; }
operation=${1:-refuse}
[[ $# == 1 && ( $operation == bootstrap || $operation == verify ) ]] || fail 'choose bootstrap or verify explicitly'
[[ ${LEASEBOOK_OPERATOR:-} =~ ^[A-Za-z0-9@._+-]{3,120}$ ]] || fail 'set LEASEBOOK_OPERATOR to an accountable operator identifier'
[[ ${PGHOST:-} =~ ^[a-z0-9][a-z0-9-]*\.postgres\.database\.azure\.com$ ]] || fail 'set PGHOST to the explicitly selected Azure server FQDN'
[[ ${LEASEBOOK_CONFIRM_HOST:-} == "$PGHOST" ]] || fail 'LEASEBOOK_CONFIRM_HOST must match the selected server'
export PGDATABASE=leasebook PGPORT=5432 PGSSLMODE=require PGCONNECT_TIMEOUT=15
export PGAPPNAME=leasebook-dbadmin
# Do not inherit client options that could log queries or redirect connections.
unset PGHOSTADDR PGSERVICE PGSERVICEFILE PGOPTIONS PGPASSFILE
export PSQL_HISTORY=/dev/null
printf 'dbadmin: start operation=%s host=%s operator=%s utc=%s\n' "$operation" "$PGHOST" "$LEASEBOOK_OPERATOR" "$(date -u +%FT%TZ)"

if [[ $operation == bootstrap ]]; then
  [[ ${PGUSER:-} =~ ^[A-Za-z][A-Za-z0-9_]{0,62}$ ]] || fail 'set the administrator login in PGUSER'
  [[ $PGUSER != leasebook_* ]] || fail 'bootstrap requires the server administrator'
  for name in PGPASSWORD LEASEBOOK_MIGRATOR_PASSWORD LEASEBOOK_APP_PASSWORD LEASEBOOK_OPS_PASSWORD; do
    [[ -n ${!name:-} ]] || fail "missing secret-backed variable $name"
    # psql password prompts are line based. Bound length below their input limit.
    value=${!name}
    [[ ${#value} -ge 16 && ${#value} -le 80 && $value != *$'\n'* && $value != *$'\r'* ]] || fail "invalid password length or newline in $name (16-80 bytes required)"
  done
  unset value
  # \password encrypts in the client; cleartext never enters SQL or server statement logs.
  # One transaction includes roles, passwords and grants; disconnect/error rolls it all back.
  if printf '%s\n' "$LEASEBOOK_MIGRATOR_PASSWORD" "$LEASEBOOK_MIGRATOR_PASSWORD" \
      "$LEASEBOOK_APP_PASSWORD" "$LEASEBOOK_APP_PASSWORD" "$LEASEBOOK_OPS_PASSWORD" "$LEASEBOOK_OPS_PASSWORD" |
      psql -X -w -q -1 -v ON_ERROR_STOP=1 -v ECHO=none -v ECHO_HIDDEN=off \
        -f /opt/leasebook-dbadmin/azure-bootstrap.sql 2>/dev/null; then
    printf 'dbadmin: bootstrap committed\n'
  else
    fail 'bootstrap failed; inspect execution/system status and database role state before retry; commit outcome may be unknown after connection loss'
  fi
else
  [[ ${LEASEBOOK_ORG_ID:-} =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || fail 'verify requires LEASEBOOK_ORG_ID'
  [[ -n ${LEASEBOOK_OPS_PASSWORD:-} ]] || fail 'missing ops password'
  export PGUSER=leasebook_ops PGPASSWORD=$LEASEBOOK_OPS_PASSWORD
  unset LEASEBOOK_MIGRATOR_PASSWORD LEASEBOOK_APP_PASSWORD LEASEBOOK_OPS_PASSWORD
  psql -X -w -q -v ON_ERROR_STOP=1 -f /opt/leasebook-dbadmin/verify.sql 2>/dev/null || fail 'verification failed; inspect target, credentials, schema and organization before retry'
fi
printf 'dbadmin: complete operation=%s host=%s utc=%s\n' "$operation" "$PGHOST" "$(date -u +%FT%TZ)"
