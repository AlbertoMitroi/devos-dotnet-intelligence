#!/usr/bin/env bash
# Turns a fixture directory into a Git repository with fixed author and committer data,
# so its commit id is identical on every host. Runs inside the engine image (verify scripts, tests).
# Usage: fixture-repo.sh <fixture-dir> <target-dir>
set -euo pipefail
src="$1"
dst="$2"
if [ -e "$dst" ] && [ -n "$(ls -A "$dst" 2>/dev/null)" ]; then
  echo "target '$dst' is not empty" >&2
  exit 1
fi
mkdir -p "$dst"
cp -R "$src"/. "$dst"/
export GIT_AUTHOR_NAME="Fixture" GIT_AUTHOR_EMAIL="fixture@example.invalid" GIT_AUTHOR_DATE="2026-01-01T00:00:00Z"
export GIT_COMMITTER_NAME="Fixture" GIT_COMMITTER_EMAIL="fixture@example.invalid" GIT_COMMITTER_DATE="2026-01-01T00:00:00Z"
export GIT_CONFIG_NOSYSTEM=1 HOME="${TMPDIR:-/tmp}"
git -C "$dst" init -q -b main
git -C "$dst" config core.fileMode false
git -C "$dst" config core.autocrlf false
git -C "$dst" add -A
git -C "$dst" commit -q -m "fixture"
git -C "$dst" rev-parse HEAD
