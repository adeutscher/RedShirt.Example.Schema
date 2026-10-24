#!/bin/bash

cd "$(readlink -f "$(dirname "${0}")")"

ENV_FILE="${ENV_FILE:-.env}"
DOCKER_IMAGE="${DOCKER_IMAGE:-example-schema:local}"
TESTS_ENABLE="${TESTS_ENABLE:-0}"

if [ ! -f "${ENV_FILE}" ]; then
  echo "${ENV_FILE} not found (copy .env.example to ${ENV_FILE} and set values)" >&2
  exit 1
fi

docker build \
  --build-arg "TESTS_ENABLE=${TESTS_ENABLE}" \
  -t "${DOCKER_IMAGE}" \
  .

docker run --rm \
  --env-file "${ENV_FILE}" \
  --add-host=host.docker.internal:host-gateway \
  "${DOCKER_IMAGE}" "$@"
