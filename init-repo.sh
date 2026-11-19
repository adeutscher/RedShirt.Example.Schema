#!/bin/bash

# Initialise this template repository as a new schema module.
# This is destructive: files are edited and renamed in place.
#
# The current namespace is read from the solution file at the repository root, and the current label is read from
# the environment variable names in Program.cs, so the script can be run again to move an initialised repository to
# another namespace and/or label.
#
# Usage: ./init-repo.sh New.Namespace NEW_LABEL

cd "$(dirname "${0}")" || exit 1

NAMESPACE_NEW="${1}"
LABEL_NEW="${2}"

usage() {
  echo "Usage: $(basename "${0}") <new-namespace> <new-label>" >&2
}

# Escape a literal string for use in a sed regular expression.
sed_escape_pattern() {
  sed 's/[][\.*^$/]/\\&/g' <<< "${1}"
}

label_to_docker_image_name() {
  printf '%s-schema' "$(tr '[:upper:]' '[:lower:]' <<< "${1}" | tr '_' '-')"
}

label_to_schema_name() {
  tr '[:upper:]' '[:lower:]' <<< "${1}"
}

rename_project() {
  local directory
  directory="$(dirname "${1}")"
  local name
  name="$(basename "${1}")"
  local csproj
  csproj="${1}/${name}.csproj"

  local nameNew
  nameNew="${NAMESPACE_NEW}${name#"${NAMESPACE_OLD}"}"
  # Nothing to move when only the label changes. mv would fail with "are the same file".
  if [ "${nameNew}" = "${name}" ]; then
    return 0
  fi
  local csprojNew
  csprojNew="${1}/${nameNew}.csproj"

  if [ -f "${csproj}" ]; then
    mv "${csproj}" "${csprojNew}"
  fi
  mv "${1}" "${directory}/${nameNew}"
}

if [ -z "${NAMESPACE_NEW}" ] || [ -z "${LABEL_NEW}" ]; then
  usage
  exit 1
fi

# C# namespace: dotted identifiers (letter or underscore, then letters, digits, or underscores).
if ! [[ "${NAMESPACE_NEW}" =~ ^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$ ]]; then
  echo "Namespace must look like a C# namespace (e.g. MyCompany.MyApp.Schema)."
  exit 1
fi

# Label: environment variable prefix (e.g. PROJECT_MALAMUTE for PROJECT_MALAMUTE_SCHEMA_HOST).
if ! [[ "${LABEL_NEW}" =~ ^[A-Z][A-Z0-9_]*$ ]] || [[ "${LABEL_NEW}" == *_ ]]; then
  echo "Label must be uppercase letters, digits, and underscores, starting with a letter (e.g. PROJECT_MALAMUTE)."
  exit 1
fi

shopt -s nullglob
SOLUTIONS=(*.slnx)
shopt -u nullglob
if [ "${#SOLUTIONS[@]}" -ne 1 ]; then
  echo "Expected exactly one .slnx file in $(pwd) to detect the current namespace, found ${#SOLUTIONS[@]}."
  exit 1
fi
NAMESPACE_OLD="${SOLUTIONS[0]%.slnx}"

PROGRAM_FILE="src/${NAMESPACE_OLD}/Program.cs"
if [ ! -f "${PROGRAM_FILE}" ]; then
  echo "Expected ${PROGRAM_FILE} to detect the current label."
  exit 1
fi
LABEL_OLD="$(sed -n 's/.*"\([A-Z][A-Z0-9_]*\)_SCHEMA_HOST".*/\1/p' "${PROGRAM_FILE}" | head -n 1)"
if [ -z "${LABEL_OLD}" ]; then
  echo "Could not find a *_SCHEMA_HOST variable name in ${PROGRAM_FILE} to detect the current label."
  exit 1
fi

if [ "${NAMESPACE_NEW}" = "${NAMESPACE_OLD}" ] && [ "${LABEL_NEW}" = "${LABEL_OLD}" ]; then
  echo "New namespace or label must differ from ${NAMESPACE_OLD} / ${LABEL_OLD}."
  exit 1
fi

DOCKER_IMAGE_NAME_OLD="$(label_to_docker_image_name "${LABEL_OLD}")"
DOCKER_IMAGE_NAME_NEW="$(label_to_docker_image_name "${LABEL_NEW}")"

SCHEMA_NAME_OLD="$(label_to_schema_name "${LABEL_OLD}")"
SCHEMA_NAME_NEW="$(label_to_schema_name "${LABEL_NEW}")"

# Match whole names only, so that Foo.Bar does not also rewrite Foo.Barn or Foo.Bar_X.
NAMESPACE_OLD_PATTERN="\\b$(sed_escape_pattern "${NAMESPACE_OLD}")\\b"
LABEL_OLD_PATTERN="\\b$(sed_escape_pattern "${LABEL_OLD}")_SCHEMA_"
DOCKER_IMAGE_NAME_OLD_PATTERN="\\b$(sed_escape_pattern "${DOCKER_IMAGE_NAME_OLD}")\\b"
SCHEMA_NAME_OLD_ESCAPED="$(sed_escape_pattern "${SCHEMA_NAME_OLD}")"

echo "Renaming namespace ${NAMESPACE_OLD} -> ${NAMESPACE_NEW}"
echo "Renaming label ${LABEL_OLD} -> ${LABEL_NEW}"
echo "Renaming default Docker image ${DOCKER_IMAGE_NAME_OLD} -> ${DOCKER_IMAGE_NAME_NEW}"
echo "Renaming default schema name ${SCHEMA_NAME_OLD} -> ${SCHEMA_NAME_NEW}"

# Paths that never hold template sources: VCS metadata, IDE state, build/test output, and this script.
PRUNE=(\( -path './.git' -o -path './.idea' -o -name 'bin' -o -name 'obj' -o -path './TestResults' -o -path './init-repo.sh' \) -prune -o)

while read -r f; do
  [ -z "${f}" ] && continue
  rename_project "${f}"
done <<< "$(find . "${PRUNE[@]}" -type d \( -name "${NAMESPACE_OLD}" -o -name "${NAMESPACE_OLD}.*" \) -print)"

if [ "${NAMESPACE_NEW}" != "${NAMESPACE_OLD}" ]; then
  mv "${NAMESPACE_OLD}.slnx" "${NAMESPACE_NEW}.slnx"
fi

# check-scripts.sh lets the seed script under Scripts/0000/ be edited only while this repository is still the template
# (template_mode=1). The first run of this script ends template mode. After that, the seed script counts as applied.
CHECK_SCRIPTS="scripts/cicd/check-scripts.sh"
TEMPLATE_MODE="$(sed -n 's/^template_mode=\([01]\)$/\1/p' "${CHECK_SCRIPTS}")"
if [ -z "${TEMPLATE_MODE}" ]; then
  echo "Could not find template_mode=0 or template_mode=1 in ${CHECK_SCRIPTS}."
  exit 1
fi

# The template's single seed script is named after the schema. It is renamed only on the first run: once the seed
# script may have been applied, renaming it would make DbUp run it again on existing databases.
SEED_SCRIPT_DIR="src/${NAMESPACE_NEW}/Scripts/0000/0000-01"
SEED_RENAME_EXPRESSIONS=()
if [ "${TEMPLATE_MODE}" = "1" ]; then
  if [ "${SCHEMA_NAME_NEW}" != "${SCHEMA_NAME_OLD}" ] && [ -f "${SEED_SCRIPT_DIR}/0000-00-00-00-${SCHEMA_NAME_OLD}.sql" ]; then
    mv "${SEED_SCRIPT_DIR}/0000-00-00-00-${SCHEMA_NAME_OLD}.sql" "${SEED_SCRIPT_DIR}/0000-00-00-00-${SCHEMA_NAME_NEW}.sql"
  fi
  SEED_RENAME_EXPRESSIONS=(-e "s/-${SCHEMA_NAME_OLD_ESCAPED}\\.sql\\b/-${SCHEMA_NAME_NEW}.sql/g")
elif [ "${SCHEMA_NAME_NEW}" != "${SCHEMA_NAME_OLD}" ]; then
  echo "Keeping the seed script name, since it may already be applied."
fi

while read -r f; do
  [ -z "${f}" ] && continue
  sed -i \
    -e "s/${NAMESPACE_OLD_PATTERN}/${NAMESPACE_NEW}/g" \
    -e "s/${LABEL_OLD_PATTERN}/${LABEL_NEW}_SCHEMA_/g" \
    -e "s/${DOCKER_IMAGE_NAME_OLD_PATTERN}/${DOCKER_IMAGE_NAME_NEW}/g" \
    -e "s/_SCHEMA_NAME=\"${SCHEMA_NAME_OLD_ESCAPED}\"/_SCHEMA_NAME=\"${SCHEMA_NAME_NEW}\"/g" \
    -e "s/_SCHEMA_NAME=${SCHEMA_NAME_OLD_ESCAPED}\\b/_SCHEMA_NAME=${SCHEMA_NAME_NEW}/g" \
    -e "s/_SCHEMA_NAME\`: \`${SCHEMA_NAME_OLD_ESCAPED}\`/_SCHEMA_NAME\`: \`${SCHEMA_NAME_NEW}\`/g" \
    -e "s/DatabaseName = \"${SCHEMA_NAME_OLD_ESCAPED}\"/DatabaseName = \"${SCHEMA_NAME_NEW}\"/g" \
    "${SEED_RENAME_EXPRESSIONS[@]}" \
    "${f}"
done <<< "$(find . "${PRUNE[@]}" -type f \( \
  -name '*.cs' -o \
  -name '*.csproj' -o \
  -name '*.md' -o \
  -name '*.slnx' -o \
  -name '*.sh' -o \
  -name 'Dockerfile' -o \
  -name '.dockerignore' -o \
  -name '.env.example' -o \
  \( -path './.github/*' -a \( -name '*.yml' -o -name '*.yaml' \) \) \
\) -print)"

if [ "${TEMPLATE_MODE}" = "1" ]; then
  sed -i 's/^template_mode=1$/template_mode=0/' "${CHECK_SCRIPTS}"
  echo "Ended template mode in ${CHECK_SCRIPTS}: the seed script is now covered by the immutability check."
fi
