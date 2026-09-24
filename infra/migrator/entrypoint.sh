#!/bin/sh
set -eu

case "${LEASEBOOK_REQUIRE_VERIFIED_POSTGRES_TLS:-false}" in
  true)
    /bin/sh "$(dirname "$0")/require-verified-postgres-tls.sh"
    ;;
  false|'')
    ;;
  *)
    echo "::error::LEASEBOOK_REQUIRE_VERIFIED_POSTGRES_TLS must be 'true' or 'false'." >&2
    exit 1
    ;;
esac

if [ "$#" -eq 0 ]; then
  echo "::error::The migrator entrypoint received no bundle command." >&2
  exit 1
fi

exec "$@"
