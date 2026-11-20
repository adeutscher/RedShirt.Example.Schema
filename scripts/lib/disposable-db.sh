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
REVIEW_SCHEMA_PASSWORD="${REVIEW_SCHEMA_PASSWORD:-schema-review-local}"

# The upgrader runs as a migration user with only the privileges that the README's Database Users section grants, so
# that a script needing more fails here before it fails against a deployed database. The password is random per run.
REVIEW_MIGRATION_USER="${REVIEW_MIGRATION_USER:-schema_migrator}"
REVIEW_MIGRATION_PASSWORD="$(od -An -tx1 -N16 /dev/urandom | tr -d ' \n')"

# Matches SchemaUpgrader.JournalTableName.
JOURNAL_TABLE_NAME="${JOURNAL_TABLE_NAME:-Patches}"
# Matches ScriptChecksums.TableName.
CHECKSUM_TABLE_NAME="${CHECKSUM_TABLE_NAME:-PatchChecksums}"

# =============================================================================
# MariaDB-specific: disposable database server (image, bootstrap env, readiness)
# =============================================================================
MARIADB_IMAGE="${MARIADB_IMAGE:-mariadb:12.3.2}"
MARIADB_ROOT_PASSWORD="${MARIADB_ROOT_PASSWORD:-${REVIEW_SCHEMA_PASSWORD}}"
# The bootstrap account, used to create the migration user and for the read-only review queries.
REVIEW_ADMIN_USER="root"
# Must match the migration user's GRANT in the README (Database Users). scripts/cicd/check-privileges.sh checks this.
MIGRATION_PRIVILEGES="SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES"

# Passwords are handed to docker as bare "-e NAME" options with the value in docker's own environment, so they never
# appear on a command line (visible to other users through ps and /proc). The mariadb clients read MYSQL_PWD.

NETWORK_NAME="schema-review-$$"
DB_CONTAINER="schema-review-db-$$"

disposable_db_cleanup() {
  docker rm -f "${DB_CONTAINER}" >/dev/null 2>&1 || true
  docker network rm "${NETWORK_NAME}" >/dev/null 2>&1 || true
}

disposable_db_start() {
  trap disposable_db_cleanup EXIT

  docker network create "${NETWORK_NAME}" >/dev/null

  MARIADB_ROOT_PASSWORD="${MARIADB_ROOT_PASSWORD}" docker run -d \
    --name "${DB_CONTAINER}" \
    --network "${NETWORK_NAME}" \
    -e MARIADB_ROOT_PASSWORD \
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
  until MYSQL_PWD="${MARIADB_ROOT_PASSWORD}" docker run --rm \
    --network "${NETWORK_NAME}" \
    -e MYSQL_PWD \
    "${MARIADB_IMAGE}" \
    mariadb \
    -h "${DB_CONTAINER}" \
    -u "${REVIEW_ADMIN_USER}" \
    -e "SELECT 1" \
    >/dev/null 2>&1
  do
    if [ "${SECONDS}" -ge "${deadline}" ]; then
      error "MariaDB did not accept TCP connections within 120 seconds"
      exit 1
    fi
    sleep 1
  done

  notice "Creating migration user ${REVIEW_MIGRATION_USER} (${MIGRATION_PRIVILEGES})..."
  db_create_migration_user

  notice "Applying schema updates as ${REVIEW_MIGRATION_USER}..."
  if ! EXAMPLE_SCHEMA_PASSWORD="${REVIEW_MIGRATION_PASSWORD}" docker run --rm \
    --network "${NETWORK_NAME}" \
    -e "EXAMPLE_SCHEMA_HOST=${DB_CONTAINER}" \
    -e "EXAMPLE_SCHEMA_NAME=${REVIEW_SCHEMA_NAME}" \
    -e "EXAMPLE_SCHEMA_USER=${REVIEW_MIGRATION_USER}" \
    -e EXAMPLE_SCHEMA_PASSWORD \
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
  MYSQL_PWD="${MARIADB_ROOT_PASSWORD}" docker exec -e MYSQL_PWD "${DB_CONTAINER}" mariadb \
    --batch \
    --skip-column-names \
    -u "${REVIEW_ADMIN_USER}" \
    "${REVIEW_SCHEMA_NAME}" \
    -e "${1}"
}

# Creates the migration user with MIGRATION_PRIVILEGES on the review schema only. The SQL holds the password, so it is
# sent on stdin rather than as a command line argument.
db_create_migration_user() {
  local user password schema
  user="$(db_quote "${REVIEW_MIGRATION_USER}")"
  password="$(db_quote "${REVIEW_MIGRATION_PASSWORD}")"
  schema="${REVIEW_SCHEMA_NAME//\`/\`\`}"

  printf "CREATE USER '%s'@'%%' IDENTIFIED BY '%s' REQUIRE SSL;\nGRANT %s ON \`%s\`.* TO '%s'@'%%';\n" \
    "${user}" "${password}" "${MIGRATION_PRIVILEGES}" "${schema}" "${user}" \
    | MYSQL_PWD="${MARIADB_ROOT_PASSWORD}" docker exec -i -e MYSQL_PWD "${DB_CONTAINER}" mariadb \
      -u "${REVIEW_ADMIN_USER}"
}

# Escapes a value for use inside a single-quoted SQL string literal.
db_quote() {
  printf '%s' "${1}" | sed -e "s/\\\\/\\\\\\\\/g" -e "s/'/''/g"
}

db_dump_schema() {
  MYSQL_PWD="${MARIADB_ROOT_PASSWORD}" docker exec -e MYSQL_PWD "${DB_CONTAINER}" mariadb-dump \
    --no-data \
    --skip-add-drop-table \
    --routines \
    --skip-comments \
    -u "${REVIEW_ADMIN_USER}" \
    "${REVIEW_SCHEMA_NAME}"
}
