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
# Optional host path to a CA certificate (PEM) for EXAMPLE_SCHEMA_SSL_MODE=VerifyCA or VerifyFull. It is mounted into
# the container, and EXAMPLE_SCHEMA_SSL_CA is set to its path there, overriding any value in ENV_FILE.
CA_FILE="${CA_FILE:-}"
CONTAINER_CA_FILE="/run/schema/ca.pem"

if [ ! -f "${ENV_FILE}" ]; then
  echo "${ENV_FILE} not found (copy .env.example to ${ENV_FILE} and set values)" >&2
  exit 1
fi

for file_var in PASSWORD_FILE CA_FILE; do
  if [ -n "${!file_var}" ] && { [ ! -f "${!file_var}" ] || [ ! -r "${!file_var}" ]; }; then
    echo "${file_var} ${!file_var} is not a readable file" >&2
    exit 1
  fi
done

# The container runs as a non-root user that cannot read a mode 600 file owned by the caller. Stage copies that it can
# read inside a private directory that other host users cannot enter.
mount_args=()
if [ -n "${PASSWORD_FILE}" ] || [ -n "${CA_FILE}" ]; then
  stage_dir="$(mktemp -d)"
  trap 'rm -rf "${stage_dir:?}"' EXIT
fi

if [ -n "${PASSWORD_FILE}" ]; then
  install -m 644 "${PASSWORD_FILE}" "${stage_dir}/password"
  mount_args+=(
    -v "${stage_dir}/password:${CONTAINER_PASSWORD_FILE}:ro"
    -e "EXAMPLE_SCHEMA_PASSWORD_FILE=${CONTAINER_PASSWORD_FILE}"
  )
fi

if [ -n "${CA_FILE}" ]; then
  install -m 644 "${CA_FILE}" "${stage_dir}/ca.pem"
  mount_args+=(
    -v "${stage_dir}/ca.pem:${CONTAINER_CA_FILE}:ro"
    -e "EXAMPLE_SCHEMA_SSL_CA=${CONTAINER_CA_FILE}"
  )
fi

docker build \
  --build-arg "TESTS_ENABLE=${TESTS_ENABLE}" \
  -t "${DOCKER_IMAGE}" \
  .

docker run --rm \
  --env-file "${ENV_FILE}" \
  ${mount_args[@]+"${mount_args[@]}"} \
  --add-host=host.docker.internal:host-gateway \
  "${DOCKER_IMAGE}" "$@"
