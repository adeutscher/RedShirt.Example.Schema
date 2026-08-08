#!/bin/bash

cd "$(readlink -f "$(dirname "${0}")")"

if [ -z "${LOCAL_SQL_PASSWORD}" ]; then
  echo "LOCAL_SQL_PASSWORD is unset" >&2
  exit 1
fi

EXAMPLE_SCHEMA_HOST="127.0.0.1" \
EXAMPLE_SCHEMA_NAME="example" \
EXAMPLE_SCHEMA_USER="root" \
EXAMPLE_SCHEMA_PASSWORD="${LOCAL_SQL_PASSWORD}" \
./update.sh
