#!/usr/bin/env bash
# The single local verify command (2.4). Needs only Docker.
# 1. builds the test stage (unit and integration tests run inside it),
# 2. builds the runtime and action images,
# 3. checks the image self-description (SDK set, runtimes, Roslyn rule),
# 4. analyzes the fixtures twice with the runtime image (read-only /src, no network) and compares the runs,
# 5. runs the action image in GitHub Action mode against a simulated workspace.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
tag="${DI_VERIFY_TAG:-dotnet-intelligence:verify}"
work="$root/.verify"
user="$(id -u):$(id -g)"

step() { printf '\n== %s\n' "$*"; }

rm -rf "$work"
mkdir -p "$work/repos" "$work/out"

step "test stage"
docker build --target test -t "$tag-test" "$root"

step "runtime and action images"
docker build --target runtime -t "$tag" "$root"
docker build --target action -t "$tag-action" "$root"

step "self-test"
docker run --rm --network none "$tag" version --json | tee "$work/version.json"
grep -q "\"roslynCompatible\":true" "$work/version.json"
grep -q '"version":"10.0.403"' "$work/version.json"
grep -q '"name":"Microsoft.NETCore.App","version":"10.0.14"' "$work/version.json"
[ "$(docker run --rm --entrypoint id "$tag" -u)" != "0" ]

step "fixture repositories"
for fixture in "$root"/tests/fixtures/*/; do
  name="$(basename "$fixture")"
  docker run --rm --user "$user" -e HOME=/tmp --entrypoint bash \
    -v "$root/tests/fixtures:/fixtures:ro" -v "$work/repos:/repos" \
    "$tag" /opt/dotnet-intelligence/scripts/fixture-repo.sh "/fixtures/$name" "/repos/$name"
done

step "fixture analyses"
for repo in "$work"/repos/*/; do
  name="$(basename "$repo")"
  # The expected exit code of the first run in the fixture's expected.yml (0 when the fixture has none).
  expected=0
  if [ -f "$root/tests/fixtures/$name/expected.yml" ]; then
    expected="$(sed -n 's/^[ -]*exitCode: *\([0-9][0-9]*\).*/\1/p' "$root/tests/fixtures/$name/expected.yml" | head -n 1)"
  fi
  for run in 1 2; do
    mkdir -p "$work/out/$name-$run"
    set +e
    docker run --rm --user "$user" \
      -v "$repo:/src:ro" -v "$work/out/$name-$run:/out" \
      "$tag" analyze --src /src --out /out > "$work/out/$name-$run.events.ndjson"
    code=$?
    set -e
    if [ "$code" -ne "$expected" ]; then
      echo "analyze $name run $run: exit code $code, expected $expected" >&2
      exit 1
    fi
    test -f "$work/out/$name-$run/manifest.json"
  done
  # Byte comparison inside the image, so the check is the same on every host.
  docker run --rm --entrypoint diff -v "$work/out:/runs:ro" "$tag" -r -x run.json "/runs/$name-1" "/runs/$name-2"
  echo "$name: two runs identical"
  docker run --rm -v "$work/out:/runs:ro" "$tag" verify "/runs/$name-1"
done

step "GitHub Action mode"
mkdir -p "$work/workspace"
cp -R "$work/repos/f00-minimal" "$work/workspace/repo"
: > "$work/workspace/github-output"
docker run --rm --network none \
  -e GITHUB_WORKSPACE=/github/workspace -e GITHUB_OUTPUT=/github/workspace/github-output \
  -e INPUT_PATH=repo -e INPUT_OUTPUT=intelligence-out -e "INPUT_FAIL-ON=failed" \
  -v "$work/workspace:/github/workspace" -w /github/workspace \
  "$tag-action" gha > "$work/action.log"
grep -qx 'status=complete' "$work/workspace/github-output"
grep -qx 'manifest-path=intelligence-out/manifest.json' "$work/workspace/github-output"
test -f "$work/workspace/intelligence-out/manifest.json"
cat "$work/workspace/github-output"

step "verify passed"
