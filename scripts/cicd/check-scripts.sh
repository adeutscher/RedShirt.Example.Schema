#!/usr/bin/env bash
#
# Checks DbUp update scripts against the rules in the README's "File Organization and Maintenance" section:
#
# * Every file under the scripts directory is a .sql file at Scripts/YYYY/YYYY-MM/YYYY-MM-DD-NN-name.sql, where the
#   directory year and month match the file name. Files that are not .sql would silently not be embedded.
# * Scripts that already exist on the base branch are never modified, renamed, or deleted. Only additions are allowed.
#
# The base branch is origin/${GITHUB_BASE_REF} on pull requests (fetched by the workflow, or here if missing), otherwise
# BASE_REF (default origin/develop). On a push, the workflow sets BASE_REF to the commit the branch pointed at before the
# push, so that pushes made directly to a branch are checked too. When the base ref is not available, the immutability check fails on CI (CI=true)
# and is skipped with a warning elsewhere.
#
# Scripts/0000/ holds the template's seed script. It is exempt from the name check, and from the immutability check only
# while template_mode=1 (see below). init-repo.sh ends template mode.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

scripts_dir="src/RedShirt.Example.Schema/Scripts"
# Scripts named before the naming rule existed. They are already applied, so renaming them would make DbUp run them
# again. Each entry is an exact path, exempt from the name check only. The immutability check still covers them.
legacy_names=()
# Scripts/0000/ holds the template's seed script, which is named before the naming rule and is always exempt from the
# name check. While template_mode=1 it is also exempt from the immutability check, so that it can be edited in place.
# init-repo.sh sets template_mode=0 on its first run. From then on the seed script counts as applied.
template_mode=1
template_dir="${scripts_dir}/0000/"

failed=0

echo "==> update script names"
while IFS= read -r path; do
  [ -z "${path}" ] && continue
  for legacy in "${legacy_names[@]}"; do
    [ "${path}" = "${legacy}" ] && continue 2
  done
  [[ "${path}" == "${template_dir}"* ]] && continue

  relative="${path#"${scripts_dir}/"}"
  if [[ "${relative}" =~ ^([0-9]{4})/([0-9]{4})-([0-9]{2})/([0-9]{4})-([0-9]{2})-([0-9]{2})-([0-9]{2})-[a-z0-9]+(-[a-z0-9]+)*\.sql$ ]]; then
    if [ "${BASH_REMATCH[1]}" != "${BASH_REMATCH[2]}" ] \
      || [ "${BASH_REMATCH[1]}" != "${BASH_REMATCH[4]}" ] \
      || [ "${BASH_REMATCH[3]}" != "${BASH_REMATCH[5]}" ]; then
      echo "ERROR: ${path}: directory year/month does not match the file name date." >&2
      failed=1
    fi
  else
    echo "ERROR: ${path}: expected Scripts/YYYY/YYYY-MM/YYYY-MM-DD-NN-lowercase-name.sql" >&2
    failed=1
  fi
done <<< "$(git ls-files --cached --others --exclude-standard -- "${scripts_dir}")"

echo "==> applied update scripts are unchanged"
base_ref="${BASE_REF:-origin/develop}"
if [ -n "${GITHUB_BASE_REF:-}" ]; then
  base_ref="origin/${GITHUB_BASE_REF}"
  # The workflow fetches the base branch before this runs, since checkout does not keep credentials for later steps.
  # Fetch it here only when it is missing, such as when this script runs in another pipeline.
  if ! git rev-parse --verify --quiet "${base_ref}^{commit}" >/dev/null; then
    git fetch --quiet --no-tags --depth=1 origin \
      "+refs/heads/${GITHUB_BASE_REF}:refs/remotes/origin/${GITHUB_BASE_REF}"
  fi
fi

if ! git rev-parse --verify --quiet "${base_ref}^{commit}" >/dev/null; then
  # On CI a missing base ref means the check could not run, so fail rather than pass without checking anything.
  if [ "${CI:-}" = "true" ]; then
    echo "ERROR: ${base_ref} not available, so applied update scripts cannot be checked." >&2
    failed=1
  else
    echo "WARNING: ${base_ref} not available, skipping (set BASE_REF to compare against another ref)." >&2
  fi
else
  # Compare against the merge base so that scripts added to the base branch after this branch was cut are not reported
  # as deleted. On a shallow pull request checkout, HEAD is the merge commit and the base ref is compared directly.
  compare_ref="$(git merge-base "${base_ref}" HEAD 2>/dev/null || echo "${base_ref}")"
  while IFS=$'\t' read -r status path; do
    [ -z "${status}" ] && continue
    [ "${template_mode}" = "1" ] && [[ "${path}" == "${template_dir}"* ]] && continue
    [ "${status}" = "A" ] && continue
    echo "ERROR: ${path}: applied update script changed (status ${status}) relative to ${base_ref}." \
      "Add a new script instead." >&2
    failed=1
  done <<< "$(git diff --name-status --no-renames "${compare_ref}" -- "${scripts_dir}")"
fi

if [ "${failed}" -ne 0 ]; then
  exit 1
fi
