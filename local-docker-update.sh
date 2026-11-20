#!/bin/bash

set -euo pipefail

script_dir="$(readlink -f "$(dirname "${0}")")" && cd "${script_dir}" || exit 1

if [ -z "${LOCAL_SQL_PASSWORD:-}" ]; then
  echo "LOCAL_SQL_PASSWORD is unset" >&2
  exit 1
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "${work_dir:?}"' EXIT

cat >"${work_dir}/env" <<ENV
EXAMPLE_SCHEMA_HOST=host.docker.internal
EXAMPLE_SCHEMA_NAME=example
EXAMPLE_SCHEMA_USER=${LOCAL_SQL_USER:-root}
EXAMPLE_SCHEMA_SSL_MODE=${EXAMPLE_SCHEMA_SSL_MODE:-Required}
EXAMPLE_SCHEMA_JOURNAL_SCHEMA_NAME=${EXAMPLE_SCHEMA_JOURNAL_SCHEMA_NAME:-}
EXAMPLE_SCHEMA_JOURNAL_TABLE_NAME=${EXAMPLE_SCHEMA_JOURNAL_TABLE_NAME:-}
ENV

# The password goes in a file that docker-update.sh mounts, so that it is not visible in docker inspect.
(umask 077 && printf '%s' "${LOCAL_SQL_PASSWORD}" >"${work_dir}/password")

# EXAMPLE_SCHEMA_SSL_CA is a host path here, as for local-update.sh. docker-update.sh mounts the file and points the
# container at its path inside the container.
ENV_FILE="${work_dir}/env" PASSWORD_FILE="${work_dir}/password" CA_FILE="${EXAMPLE_SCHEMA_SSL_CA:-}" \
  ./docker-update.sh "$@"
