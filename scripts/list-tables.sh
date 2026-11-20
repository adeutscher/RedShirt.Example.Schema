#!/bin/bash
#
# Spins up a disposable MariaDB container, applies embedded schema updates, then lists
# the table names in the applied schema, one per line.
#
# MariaDB-specific sections live in scripts/lib/disposable-db.sh.

set -euo pipefail

ROOT="$(readlink -f "$(dirname "${0}")/..")"
cd "${ROOT}"

# shellcheck source=scripts/lib/messages.sh
. "${ROOT}/scripts/lib/messages.sh"

OUTPUT_PATH=""
INCLUDE_JOURNAL=0
while [ $# -gt 0 ]; do
  case "${1}" in
    -o)
      shift
      OUTPUT_PATH="${1:-}"
      if [ -z "${OUTPUT_PATH}" ]; then
        error "Missing path after -o"
        exit 1
      fi
      ;;
    --all)
      INCLUDE_JOURNAL=1
      ;;
    -h | --help)
      echo "Usage: $(basename "${0}") [--all] [-o output.txt]" >&2
      echo "  Writes applied table names to stdout, or to output.txt when -o is set." >&2
      echo "  --all  Include the DbUp journal and script checksum tables from the journal schema." >&2
      exit 0
      ;;
    *)
      error "Unknown argument: ${1}"
      exit 1
      ;;
  esac
  shift
done

# shellcheck source=scripts/lib/disposable-db.sh
. "${ROOT}/scripts/lib/disposable-db.sh"

disposable_db_start

# The journal and checksum tables are in their own schema, so the application schema lists only application tables.
schemas="DATABASE()"
if [ "${INCLUDE_JOURNAL}" -eq 1 ]; then
  schemas="${schemas}, '$(db_quote "${REVIEW_JOURNAL_SCHEMA}")'"
fi
query="SELECT TABLE_NAME FROM information_schema.TABLES
  WHERE TABLE_SCHEMA IN (${schemas}) AND TABLE_TYPE = 'BASE TABLE'
  ORDER BY TABLE_NAME"

if [ -n "${OUTPUT_PATH}" ]; then
  db_query "${query}" >"${OUTPUT_PATH}"
  success "Wrote ${OUTPUT_PATH}"
else
  db_query "${query}"
fi
