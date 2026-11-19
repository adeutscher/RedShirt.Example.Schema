#!/bin/bash

set -euo pipefail

script_dir="$(readlink -f "$(dirname "${0}")")" && cd "${script_dir}" || exit 1

ENV_FILE="${ENV_FILE:-.env}"
DOCKER_IMAGE="${DOCKER_IMAGE:-example-schema:local}"
TESTS_ENABLE="${TESTS_ENABLE:-0}"
# Optional host path to a file holding the database password. It is mounted into the container rather than passed as
# an environment variable, so that it does not show up in docker inspect. Leave EXAMPLE_SCHEMA_PASSWORD out of
# ENV_FILE when using it.
PASSWORD_FILE="${PASSWORD_FILE:-}"
CONTAINER_PASSWORD_FILE="/run/secrets/schema-password"

if [ ! -f "${ENV_FILE}" ]; then
  echo "${ENV_FILE} not found (copy .env.example to ${ENV_FILE} and set values)" >&2
  exit 1
fi

password_args=()
if [ -n "${PASSWORD_FILE}" ]; then
  if [ ! -r "${PASSWORD_FILE}" ]; then
    echo "PASSWORD_FILE ${PASSWORD_FILE} is not a readable file" >&2
    exit 1
  fi

  # The container runs as a non-root user that cannot read a mode 600 file owned by the caller. Stage a copy that it
  # can read inside a private directory that other host users cannot enter.
  password_dir="$(mktemp -d)"
  trap 'rm -rf "${password_dir:?}"' EXIT
  install -m 644 "${PASSWORD_FILE}" "${password_dir}/password"

  password_args=(
    -v "${password_dir}/password:${CONTAINER_PASSWORD_FILE}:ro"
    -e "EXAMPLE_SCHEMA_PASSWORD_FILE=${CONTAINER_PASSWORD_FILE}"
  )
fi

docker build \
  --build-arg "TESTS_ENABLE=${TESTS_ENABLE}" \
  -t "${DOCKER_IMAGE}" \
  .

docker run --rm \
  --env-file "${ENV_FILE}" \
  ${password_args[@]+"${password_args[@]}"} \
  --add-host=host.docker.internal:host-gateway \
  "${DOCKER_IMAGE}" "$@"
