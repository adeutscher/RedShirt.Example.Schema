#!/bin/bash

NAMESPACE_OLD="RedShirt.Example.Schema"
LABEL_OLD="EXAMPLE"

NAMESPACE_NEW="${1}"
LABEL_NEW="${2}"

usage() {
  echo "Usage: $(basename "${0}") <new-namespace> <new-label>" >&2
}

rename_project() {
  local directory
  directory="$(dirname "${1}")"
  local name
  name="$(basename "${1}")"
  local csproj
  csproj="${1}/${name}.csproj"

  local nameNew
  nameNew="$(sed "s/${NAMESPACE_OLD}/${NAMESPACE_NEW}/g" <<< "${name}")"
  local csprojNew
  csprojNew="${1}/${nameNew}.csproj"

  mv "${csproj}" "${csprojNew}"
  mv "${1}" "${directory}/${nameNew}"
}

if [ -z "${NAMESPACE_NEW}" ] || [ -z "${LABEL_NEW}" ]; then
  usage
  exit 1
fi

while read -r f; do
  rename_project "${f}"
done <<< "$(find . -type d -name "${NAMESPACE_OLD}*")"

mv "${NAMESPACE_OLD}.slnx" "${NAMESPACE_NEW}.slnx"

while read -r f; do
  [ -z "${f}" ] && continue
  sed -i "s/${NAMESPACE_OLD}/${NAMESPACE_NEW}/g" "${f}"
  sed -i "s/${LABEL_OLD}_/${LABEL_NEW}_/g" "${f}"
done <<< "$(find . \( \
  -name '*.cs' -o \
  -name '*.csproj' -o \
  -name '*.md' -o \
  -name '*.slnx' -o \
  -name '*.sh' -o \
  \( -path './.github/*' -a \( -name '*.yml' -o -name '*.yaml' \) \) \
\))"
