# Update Script Lint

`scripts/cicd/check-scripts.sh` enforces the rules in the
[File Organization and Maintenance](../README.md#file-organization-and-maintenance) section of the README. It runs as
part of `scripts/cicd/lint.sh`, so it runs in the Lint step of CI before the build.

The script runs two independent checks. It reports every problem it finds, then exits with status 1 if there were any.

## Scope

* The scripts directory is `src/RedShirt.Example.Schema/Scripts`. `init-repo.sh` rewrites the namespace in `*.sh`
  files, so the path follows a namespace change.
* `Scripts/0000/` holds the template's seed script. It is always exempt from the name check. It is exempt from the
  immutability check only while `template_mode=1` in `check-scripts.sh`, so that it can be edited in place while this
  repository is a template. The first run of `init-repo.sh` sets `template_mode=0`, after which the seed script is
  checked like any other applied script.
* `legacy_names` in `check-scripts.sh` lists exact paths that are exempt from the name check only. It is for scripts
  that were applied before the naming rule existed, since renaming an applied script makes DbUp run it again. The
  immutability check still covers them. Never add new scripts to the list.

## Check 1: File Names

The file list comes from `git ls-files --cached --others --exclude-standard`. That covers tracked files and new files
that have not been staged yet, and leaves out ignored files, so a badly named script is caught locally before
`git add`.

Each path, relative to `Scripts/`, must match:

```text
YYYY/YYYY-MM/YYYY-MM-DD-NN-name.sql
```

* `name` is lowercase letters and digits, in groups separated by single hyphens.
* The years in both directories and the file name must match, and the month in the directory must match the month in
  the file name. The regular expression captures each part and the script compares them.

| Path under `Scripts/`                      | Result                           |
| ------------------------------------------ | -------------------------------- |
| `2026/2026-10/2026-10-24-01-add-notes.sql` | Pass                             |
| `2026/2026-09/2026-10-24-01-add-notes.sql` | Fail: directory month is `09`    |
| `2026/2026-10/2026-10-24-01-Add_Notes.sql` | Fail: uppercase and underscore   |
| `2026/2026-10/2026-10-24-add-notes.sql`    | Fail: missing `NN` sequence      |
| `2026/2026-10/notes.txt`                   | Fail: not `.sql`, never embedded |

Files that are not `.sql` fail because the project only embeds `Scripts/**/*.sql`. Without this check, a misnamed file
would silently be left out of the build.

## Check 2: Applied Scripts Are Unchanged

Scripts that already exist on the base branch must not be modified, renamed, or deleted. Only additions are allowed.

### Choosing the Base

* **Pull request in CI**: GitHub sets `GITHUB_BASE_REF` to the pull request's target branch. The workflow checks out
  the full history (`fetch-depth: 0`, which gitleaks needs), and its "Fetch base for script checks" step also fetches
  that branch into `refs/remotes/origin/<branch>`, with the token passed to that one command, since checkout does not
  keep credentials for later steps. If the branch is still missing, the script fetches it itself, with `--depth=1`
  only when the clone is already shallow.
* **Push in CI**: the same step fetches the commit the branch pointed at before the push (`github.event.before`) into
  `refs/ci/push-before` and sets `BASE_REF` to it, so commits pushed straight to `develop` are checked as well. A push
  that creates a branch has no previous commit, so `BASE_REF` is set to `HEAD` and there is nothing to compare.
* **Locally**: `BASE_REF` if set, otherwise `origin/develop`.
* **Base not available**: on CI (`CI=true`, which GitHub Actions sets), the check fails, since a check that could not
  run must not pass. Elsewhere it prints a warning and skips the immutability check. This covers local clones without
  the remote branch.

### Choosing the Commit to Compare

* **Locally**: the script uses `git merge-base <base> HEAD`. If the base branch has gained scripts since the current
  branch was cut, comparing against the base directly would report those scripts as deleted. The merge base avoids
  that false failure.
* **In CI**: the clone has full history, so the merge base is used here too. On a pull request, checkout uses the merge
  commit (`refs/pull/N/merge`), so the merge base is the base branch's tip. On a push it is normally the commit before
  the push.
* **Shallow clones** (other pipelines): `git merge-base` fails and the script compares against the base directly. On a
  pull request that is still correct, because the merge commit already contains every script on the base.

### Comparing

```bash
git diff --name-status --no-renames <compare> -- <scripts dir>
```

* The comparison is against the working tree, not `HEAD`, so uncommitted edits to an applied script are caught too.
* `--no-renames` reports a rename as a delete plus an add. A rename changes the name that DbUp records in the journal,
  so it must fail.
* Only status `A` (added) passes. Modified, deleted, and type-changed files each print an error naming the file and
  the base it was compared against.

## When It Runs

* **Pull requests**: the check is enforced here.
* **Pushes to `develop`**: `GITHUB_BASE_REF` is empty on push events, so the base is `origin/develop`, which is the
  pushed commit itself. The immutability check therefore finds nothing. The rules only hold if `develop` requires pull
  requests through branch protection, since a direct push skips the check.
* **Locally**: run the whole lint stage, or the script on its own against another base:

```bash
./scripts/cicd/lint.sh
```

```bash
BASE_REF=origin/main ./scripts/cicd/check-scripts.sh
```

## Limitations

* **Backdated scripts**: a new script whose name sorts before scripts already on the base passes both checks. DbUp
  runs it on existing databases after the scripts they already have, but on a fresh database it runs in name order,
  before them. The two can end up with different schemas.
* **Sequence numbers**: `NN` values are not checked for uniqueness or order within a day.
* **Contents**: the SQL inside a script is not inspected.
