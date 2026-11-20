#!/usr/bin/env bash
#
# Checks that the migration user's privileges are the same in the three places that list them:
#
# * the migration user's GRANTs in the README (Database Users), which deployers copy,
# * MIGRATION_PRIVILEGES and JOURNAL_PRIVILEGES in scripts/lib/disposable-db.sh, used by list-tables.sh and
#   suggest-dto.sh,
# * MigrationPrivileges and JournalPrivileges in the integration tests.
#
# The scripts and tests only prove that the documented grants are enough while all three agree.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

echo "==> migration privileges agree"

# One privilege per line, uppercased and sorted, so that case, spacing and order do not count as a difference.
normalise() {
  tr '[:lower:]' '[:upper:]' | tr ',' '\n' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e '/^$/d' | sort
}

# The README statements may wrap, so join each GRANT ... ; statement onto one line first. The migration user is the
# one whose name ends in _migrator. Its grant on the journal schema names a schema ending in _changes.
readme_grants="$(awk '
  /^GRANT / { statement = "" ; collecting = 1 }
  collecting { statement = statement " " $0 }
  collecting && /;[[:space:]]*$/ { print statement ; collecting = 0 }
' README.md | { grep "_migrator'" || true; })"

readme_privileges() {
  sed -n 's/^ *GRANT \(.*[^ ]\) *ON .*/\1/p' | head -n 1
}

readme_migration="$({ grep -v '_changes' || true; } <<< "${readme_grants}" | readme_privileges)"
readme_journal="$({ grep '_changes' || true; } <<< "${readme_grants}" | readme_privileges)"

disposable_value() {
  sed -n "s/^${1}=\"\\(.*\\)\"\$/\\1/p" scripts/lib/disposable-db.sh
}

tests_value() {
  local f value=""
  for f in test/*/Tests/SchemaUpgradeTests.cs; do
    [ -f "${f}" ] || continue
    value="$(sed -n "s/.*const string ${1} = \"\\(.*\\)\";.*/\\1/p" "${f}")"
  done
  printf '%s' "${value}"
}

failed=0

# Compares one grant's privileges across the README, disposable-db.sh and the tests.
compare() {
  local label="${1}" readme="${2}" disposable="${3}" tests="${4}" source missing=0

  for source in "README.md:${readme}" "scripts/lib/disposable-db.sh:${disposable}" "SchemaUpgradeTests.cs:${tests}"; do
    if [ -z "${source#*:}" ]; then
      echo "ERROR: could not find the ${label} privileges in ${source%%:*}." >&2
      missing=1
    fi
  done
  if [ "${missing}" -ne 0 ]; then
    failed=1
    return
  fi

  for source in "scripts/lib/disposable-db.sh:${disposable}" "SchemaUpgradeTests.cs:${tests}"; do
    if [ "$(normalise <<< "${source#*:}")" != "$(normalise <<< "${readme}")" ]; then
      echo "ERROR: ${label} privileges in ${source%%:*} differ from the README grant." >&2
      echo "  README.md: ${readme}" >&2
      echo "  ${source%%:*}: ${source#*:}" >&2
      failed=1
    fi
  done
}

compare "migration" "${readme_migration}" "$(disposable_value MIGRATION_PRIVILEGES)" "$(tests_value MigrationPrivileges)"
compare "journal" "${readme_journal}" "$(disposable_value JOURNAL_PRIVILEGES)" "$(tests_value JournalPrivileges)"

exit "${failed}"
