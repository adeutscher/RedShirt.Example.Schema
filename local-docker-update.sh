#!/bin/bash

cd "$(readlink -f "$(dirname "${0}")")"

if [ -z "${LOCAL_SQL_PASSWORD}" ]; then
  echo "LOCAL_SQL_PASSWORD is unset" >&2
  exit 1
fi

ENV_FILE="$(mktemp)"
trap 'rm -f "${ENV_FILE}"' EXIT

cat >"${ENV_FILE}" <<EOF
EXAMPLE_SCHEMA_HOST=host.docker.internal
EXAMPLE_SCHEMA_NAME=example
EXAMPLE_SCHEMA_USER=root
EXAMPLE_SCHEMA_PASSWORD=${LOCAL_SQL_PASSWORD}
EOF

ENV_FILE="${ENV_FILE}" ./docker-update.sh "$@"
