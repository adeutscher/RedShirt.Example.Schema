#!/bin/bash
#
# Spins up a disposable MariaDB container, applies embedded schema updates via the
# schema upgrader Docker image (same build path as docker-update.sh), then prints a
# schema-only dump for human review.
#
# MariaDB-specific sections are marked below. When switching database technology,
# replace those blocks and point the upgrader step at an image built for that engine.

set -euo pipefail

ROOT="$(readlink -f "$(dirname "${0}")/..")"
cd "${ROOT}"

OUTPUT_PATH=""
while [ $# -gt 0 ]; do
  case "${1}" in
    -o)
      shift
      OUTPUT_PATH="${1:-}"
      if [ -z "${OUTPUT_PATH}" ]; then
        echo "Missing path after -o" >&2
        exit 1
      fi
      ;;
    -h | --help)
      echo "Usage: $(basename "${0}") [-o output.sql]" >&2
      echo "  Writes a schema-only SQL dump to stdout, or to output.sql when -o is set." >&2
      exit 0
      ;;
    *)
      echo "Unknown argument: ${1}" >&2
      exit 1
      ;;
  esac
  shift
done

DOCKER_IMAGE="${DOCKER_IMAGE:-example-schema:local}"
TESTS_ENABLE="${TESTS_ENABLE:-0}"

# Defaults align with EXAMPLE_SCHEMA_* / local-update.sh. Override when reviewing another target name.
REVIEW_SCHEMA_NAME="${EXAMPLE_SCHEMA_NAME:-example}"
REVIEW_SCHEMA_USER="${EXAMPLE_SCHEMA_USER:-root}"
REVIEW_SCHEMA_PASSWORD="${REVIEW_SCHEMA_PASSWORD:-schema-review-local}"

# =============================================================================
# MariaDB-specific: disposable database server (image, bootstrap env, readiness)
# =============================================================================
MARIADB_IMAGE="${MARIADB_IMAGE:-mariadb:12.3.2}"
MARIADB_ROOT_PASSWORD="${MARIADB_ROOT_PASSWORD:-${REVIEW_SCHEMA_PASSWORD}}"

NETWORK_NAME="schema-review-$$"
DB_CONTAINER="schema-review-db-$$"

cleanup() {
  docker rm -f "${DB_CONTAINER}" >/dev/null 2>&1 || true
  docker network rm "${NETWORK_NAME}" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker network create "${NETWORK_NAME}" >/dev/null

docker run -d \
  --name "${DB_CONTAINER}" \
  --network "${NETWORK_NAME}" \
  -e "MARIADB_ROOT_PASSWORD=${MARIADB_ROOT_PASSWORD}" \
  -e "MARIADB_DATABASE=${REVIEW_SCHEMA_NAME}" \
  "${MARIADB_IMAGE}" >/dev/null

echo "Waiting for MariaDB (${MARIADB_IMAGE})..." >&2
deadline=$((SECONDS + 120))
until docker exec "${DB_CONTAINER}" mariadb-admin ping \
  -h localhost \
  -u root \
  "-p${MARIADB_ROOT_PASSWORD}" \
  --silent >/dev/null 2>&1
do
  if [ "${SECONDS}" -ge "${deadline}" ]; then
    echo "MariaDB did not become ready within 120 seconds" >&2
    exit 1
  fi
  sleep 1
done

echo "Building schema upgrader image (${DOCKER_IMAGE})..." >&2
docker build \
  --build-arg "TESTS_ENABLE=${TESTS_ENABLE}" \
  -t "${DOCKER_IMAGE}" \
  . >&2

echo "Applying schema updates..." >&2
docker run --rm \
  --network "${NETWORK_NAME}" \
  -e "EXAMPLE_SCHEMA_HOST=${DB_CONTAINER}" \
  -e "EXAMPLE_SCHEMA_NAME=${REVIEW_SCHEMA_NAME}" \
  -e "EXAMPLE_SCHEMA_USER=${REVIEW_SCHEMA_USER}" \
  -e "EXAMPLE_SCHEMA_PASSWORD=${MARIADB_ROOT_PASSWORD}" \
  "${DOCKER_IMAGE}" >&2

# =============================================================================
# MariaDB-specific: schema dump command (replace with pg_dump, sqlpackage, etc.)
# =============================================================================
dump_schema() {
  docker exec "${DB_CONTAINER}" mariadb-dump \
    --no-data \
    --skip-add-drop-table \
    --routines \
    --skip-comments \
    -u "${REVIEW_SCHEMA_USER}" \
    "-p${MARIADB_ROOT_PASSWORD}" \
    "${REVIEW_SCHEMA_NAME}"
}

if [ -n "${OUTPUT_PATH}" ]; then
  {
    printf '%s\n' "-- Applied schema review dump (schema only, no data)"
    printf '%s\n' "-- Generated: $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
    printf '%s\n' "-- Source: ${MARIADB_IMAGE} + embedded DbUp scripts"
    printf '%s\n' ""
    dump_schema
  } >"${OUTPUT_PATH}"
  echo "Wrote ${OUTPUT_PATH}" >&2
else
  printf '%s\n' "-- Applied schema review dump (schema only, no data)"
  printf '%s\n' "-- Generated: $(date -u +"%Y-%m-%dT%H:%M:%SZ")"
  printf '%s\n' "-- Source: ${MARIADB_IMAGE} + embedded DbUp scripts"
  printf '%s\n' ""
  dump_schema
fi
