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

# The upgrader runs as a migration user with only the privileges that the README's Database Users section grants, so
# that a script needing more fails here before it fails against a deployed database. The password is random per run.
REVIEW_MIGRATION_USER="${REVIEW_MIGRATION_USER:-schema_migrator}"
REVIEW_MIGRATION_PASSWORD="$(od -An -tx1 -N16 /dev/urandom | tr -d ' \n')"

# Holds the journal and checksum tables. Matches SchemaUpgrader.DefaultJournalSchema.
REVIEW_JOURNAL_SCHEMA="${REVIEW_SCHEMA_NAME}_changes"

# =============================================================================
# MariaDB-specific: disposable database server (image, bootstrap env, readiness)
# =============================================================================
MARIADB_IMAGE="${MARIADB_IMAGE:-mariadb:12.3.3}"
# Random per run, and never taken from the caller's environment, so that a password that is set there for another
# server does not end up in this container's configuration, which docker inspect shows.
MARIADB_ROOT_PASSWORD="$(od -An -tx1 -N16 /dev/urandom | tr -d ' \n')"
# The bootstrap account, used to create the migration user and for the read-only review queries.
REVIEW_ADMIN_USER="root"
# Must match the migration user's GRANT in the README (Database Users). scripts/cicd/check-privileges.sh checks this.
MIGRATION_PRIVILEGES="SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES"
# Must match the migration user's GRANT on the journal schema in the README. Also checked by check-privileges.sh.
JOURNAL_PRIVILEGES="SELECT, INSERT, CREATE"

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

  # The network is internal, so the database has no route out, and the host gets no address on it, so local users and
  # processes on the host cannot reach the database either. Only containers attached to the network can.
  docker network create --internal \
    -o com.docker.network.bridge.inhibit_ipv4=true \
    "${NETWORK_NAME}" >/dev/null

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

  notice "Creating journal schema ${REVIEW_JOURNAL_SCHEMA} and migration user ${REVIEW_MIGRATION_USER}..."
  db_create_migration_user

  notice "Applying schema updates as ${REVIEW_MIGRATION_USER}..."
  # The server's certificate is generated at startup, so it cannot be verified. Required is set explicitly to accept
  # that on this internal network, which also keeps the upgrader from warning about it.
  if ! EXAMPLE_SCHEMA_PASSWORD="${REVIEW_MIGRATION_PASSWORD}" docker run --rm \
    --network "${NETWORK_NAME}" \
    -e "EXAMPLE_SCHEMA_HOST=${DB_CONTAINER}" \
    -e "EXAMPLE_SCHEMA_SSL_MODE=Required" \
    -e "EXAMPLE_SCHEMA_NAME=${REVIEW_SCHEMA_NAME}" \
    -e "EXAMPLE_SCHEMA_JOURNAL_SCHEMA_NAME=${REVIEW_JOURNAL_SCHEMA}" \
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

# Quotes a schema name for use as an identifier.
db_quote_identifier() {
  # shellcheck disable=SC2016 # The backticks are literal SQL quotes.
  printf '`%s`' "${1//\`/\`\`}"
}

# Quotes a schema name for the ON clause of a GRANT. _ and % are wildcards there, so escape them so that the grant
# covers that schema only.
db_quote_grant_schema() {
  local schema="${1//_/\\_}"
  db_quote_identifier "${schema//%/\\%}"
}

# Does what an admin does for a deployed database: creates the journal schema, and the migration user with
# MIGRATION_PRIVILEGES on the review schema and JOURNAL_PRIVILEGES on the journal schema, nothing else. The SQL holds
# the password, so it is sent on stdin rather than as a command line argument.
db_create_migration_user() {
  local user password
  user="'$(db_quote "${REVIEW_MIGRATION_USER}")'@'%'"
  password="$(db_quote "${REVIEW_MIGRATION_PASSWORD}")"

  {
    printf "CREATE DATABASE %s;\n" "$(db_quote_identifier "${REVIEW_JOURNAL_SCHEMA}")"
    printf "CREATE USER %s IDENTIFIED BY '%s' REQUIRE SSL;\n" "${user}" "${password}"
    printf "GRANT %s ON %s.* TO %s;\n" \
      "${MIGRATION_PRIVILEGES}" "$(db_quote_grant_schema "${REVIEW_SCHEMA_NAME}")" "${user}"
    printf "GRANT %s ON %s.* TO %s;\n" \
      "${JOURNAL_PRIVILEGES}" "$(db_quote_grant_schema "${REVIEW_JOURNAL_SCHEMA}")" "${user}"
  } | MYSQL_PWD="${MARIADB_ROOT_PASSWORD}" docker exec -i -e MYSQL_PWD "${DB_CONTAINER}" mariadb \
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
