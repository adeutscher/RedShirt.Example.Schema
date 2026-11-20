#!/usr/bin/env bash
#
# Checks that the migration user's privileges are the same in the three places that list them:
#
# * the migration user's GRANT in the README (Database Users), which deployers copy,
# * MIGRATION_PRIVILEGES in scripts/lib/disposable-db.sh, used by list-tables.sh and suggest-dto.sh,
# * MigrationPrivileges in the integration tests.
#
# The scripts and tests only prove that the documented grant is enough while all three agree.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${repo_root}"

echo "==> migration privileges agree"

# One privilege per line, uppercased and sorted, so that case, spacing and order do not count as a difference.
normalise() {
  tr '[:lower:]' '[:upper:]' | tr ',' '\n' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e '/^$/d' | sort
}

# The README statement may wrap, so join each GRANT ... ; statement onto one line first. The migration user is the
# one whose name ends in _migrator.
readme="$(awk '
  /^GRANT / { statement = "" ; collecting = 1 }
  collecting { statement = statement " " $0 }
  collecting && /;[[:space:]]*$/ { print statement ; collecting = 0 }
 ' README.md | { grep "_migrator'" || true; } | sed -n 's/^ *GRANT \(.*[^ ]\) *ON .*/\1/p' | head -n 1)"

disposable="$(sed -n 's/^MIGRATION_PRIVILEGES="\(.*\)"$/\1/p' scripts/lib/disposable-db.sh)"

tests=""
for f in test/*/Tests/SchemaUpgradeTests.cs; do
  [ -f "${f}" ] || continue
  tests="$(sed -n 's/.*const string MigrationPrivileges = "\(.*\)";.*/\1/p' "${f}")"
done

failed=0
for source in "README.md:${readme}" "scripts/lib/disposable-db.sh:${disposable}" "SchemaUpgradeTests.cs:${tests}"; do
  if [ -z "${source#*:}" ]; then
    echo "ERROR: could not find the migration privileges in ${source%%:*}." >&2
    failed=1
  fi
done
if [ "${failed}" -ne 0 ]; then
  exit 1
fi

expected="$(normalise <<< "${readme}")"
for source in "scripts/lib/disposable-db.sh:${disposable}" "SchemaUpgradeTests.cs:${tests}"; do
  if [ "$(normalise <<< "${source#*:}")" != "${expected}" ]; then
    echo "ERROR: migration privileges in ${source%%:*} differ from the README grant." >&2
    echo "  README.md: ${readme}" >&2
    echo "  ${source%%:*}: ${source#*:}" >&2
    failed=1
  fi
done

exit "${failed}"
