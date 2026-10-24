#!/bin/bash
#
# Shared helpers for scripts that inspect the applied (aggregate) schema.
#
# Set ROOT to the repository root, source this file, then call disposable_db_start. It spins up a disposable MariaDB container on a private Docker
# network, builds the schema upgrader image (same build path as docker-update.sh) and applies all embedded update
# scripts. The container and network are removed when the sourcing script exits.
#
# Progress messages go to stderr so that stdout is left for the calling script's output.
#
# MariaDB-specific sections are marked below. When switching database technology, replace those blocks and point the
# upgrader step at an image built for that engine.

# shellcheck source=scripts/lib/messages.sh
. "${ROOT}/scripts/lib/messages.sh"

DOCKER_IMAGE="${DOCKER_IMAGE:-example-schema:local}"
TESTS_ENABLE="${TESTS_ENABLE:-0}"

# Defaults align with EXAMPLE_SCHEMA_* / local-update.sh. Override when reviewing another target name.
REVIEW_SCHEMA_NAME="${EXAMPLE_SCHEMA_NAME:-schema-review}"
REVIEW_SCHEMA_USER="${EXAMPLE_SCHEMA_USER:-root}"
REVIEW_SCHEMA_PASSWORD="${REVIEW_SCHEMA_PASSWORD:-schema-review-local}"

# Matches SchemaUpgrader.JournalTableName.
JOURNAL_TABLE_NAME="${JOURNAL_TABLE_NAME:-Patches}"

# =============================================================================
# MariaDB-specific: disposable database server (image, bootstrap env, readiness)
# =============================================================================
MARIADB_IMAGE="${MARIADB_IMAGE:-mariadb:12.3.2}"
MARIADB_ROOT_PASSWORD="${MARIADB_ROOT_PASSWORD:-${REVIEW_SCHEMA_PASSWORD}}"

NETWORK_NAME="schema-review-$$"
DB_CONTAINER="schema-review-db-$$"

disposable_db_cleanup() {
  docker rm -f "${DB_CONTAINER}" >/dev/null 2>&1 || true
  docker network rm "${NETWORK_NAME}" >/dev/null 2>&1 || true
}

disposable_db_start() {
  trap disposable_db_cleanup EXIT

  docker network create "${NETWORK_NAME}" >/dev/null

  docker run -d \
    --name "${DB_CONTAINER}" \
    --network "${NETWORK_NAME}" \
    -e "MARIADB_ROOT_PASSWORD=${MARIADB_ROOT_PASSWORD}" \
    -e "MARIADB_DATABASE=${REVIEW_SCHEMA_NAME}" \
    "${MARIADB_IMAGE}" >/dev/null

  notice "Building schema upgrader image (${DOCKER_IMAGE})..."
  docker build \
    --build-arg "TESTS_ENABLE=${TESTS_ENABLE}" \
    -t "${DOCKER_IMAGE}" \
    "${ROOT}" >&2

  # MariaDB-specific: readiness must use TCP from another container on the same Docker
  # network (how the upgrader connects). mariadb-admin ping against localhost inside the
  # DB container can succeed before remote TCP connections are accepted.
  notice "Waiting for MariaDB (${MARIADB_IMAGE}) on ${DB_CONTAINER}..."
  local deadline=$((SECONDS + 120))
  until docker run --rm \
    --network "${NETWORK_NAME}" \
    "${MARIADB_IMAGE}" \
    mariadb \
    -h "${DB_CONTAINER}" \
    -u "${REVIEW_SCHEMA_USER}" \
    "-p${MARIADB_ROOT_PASSWORD}" \
    -e "SELECT 1" \
    >/dev/null 2>&1
  do
    if [ "${SECONDS}" -ge "${deadline}" ]; then
      error "MariaDB did not accept TCP connections within 120 seconds"
      exit 1
    fi
    sleep 1
  done

  notice "Applying schema updates..."
  if ! docker run --rm \
    --network "${NETWORK_NAME}" \
    -e "EXAMPLE_SCHEMA_HOST=${DB_CONTAINER}" \
    -e "EXAMPLE_SCHEMA_NAME=${REVIEW_SCHEMA_NAME}" \
    -e "EXAMPLE_SCHEMA_USER=${REVIEW_SCHEMA_USER}" \
    -e "EXAMPLE_SCHEMA_PASSWORD=${MARIADB_ROOT_PASSWORD}" \
    "${DOCKER_IMAGE}" >&2
  then
    error "Schema upgrade failed"
    exit 1
  fi
}

# =============================================================================
# MariaDB-specific: queries and schema dump (replace with psql, pg_dump, sqlpackage, etc.)
# =============================================================================

# Runs a query against the review schema and prints rows as tab-separated values with no header.
db_query() {
  docker exec "${DB_CONTAINER}" mariadb \
    --batch \
    --skip-column-names \
    -u "${REVIEW_SCHEMA_USER}" \
    "-p${MARIADB_ROOT_PASSWORD}" \
    "${REVIEW_SCHEMA_NAME}" \
    -e "${1}"
}

# Escapes a value for use inside a single-quoted SQL string literal.
db_quote() {
  printf '%s' "${1}" | sed -e "s/\\\\/\\\\\\\\/g" -e "s/'/''/g"
}

db_dump_schema() {
  docker exec "${DB_CONTAINER}" mariadb-dump \
    --no-data \
    --skip-add-drop-table \
    --routines \
    --skip-comments \
    -u "${REVIEW_SCHEMA_USER}" \
    "-p${MARIADB_ROOT_PASSWORD}" \
    "${REVIEW_SCHEMA_NAME}"
}
