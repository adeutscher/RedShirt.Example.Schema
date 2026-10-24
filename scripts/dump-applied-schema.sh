#!/bin/bash
#
# Spins up a disposable MariaDB container, applies embedded schema updates via the
# schema upgrader Docker image (same build path as docker-update.sh), then prints a
# schema-only dump for human review.
#
# MariaDB-specific sections live in scripts/lib/disposable-db.sh.

set -euo pipefail

ROOT="$(readlink -f "$(dirname "${0}")/..")"
cd "${ROOT}"

# shellcheck source=scripts/lib/messages.sh
. "${ROOT}/scripts/lib/messages.sh"

OUTPUT_PATH=""
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
    -h | --help)
      echo "Usage: $(basename "${0}") [-o output.sql]" >&2
      echo "  Writes a schema-only SQL dump to stdout, or to output.sql when -o is set." >&2
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

if [ -n "${OUTPUT_PATH}" ]; then
  {
    printf '%s\n' "-- Applied schema review dump (schema only, no data)"
    printf '%s\n' "-- Generated: $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
    printf '%s\n' "-- Source: ${MARIADB_IMAGE} + embedded DbUp scripts"
    printf '%s\n' ""
    db_dump_schema
  } >"${OUTPUT_PATH}"
  success "Wrote ${OUTPUT_PATH}"
else
  printf '%s\n' "-- Applied schema review dump (schema only, no data)"
  printf '%s\n' "-- Generated: $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  printf '%s\n' "-- Source: ${MARIADB_IMAGE} + embedded DbUp scripts"
  printf '%s\n' ""
  db_dump_schema
fi
