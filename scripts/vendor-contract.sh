#!/usr/bin/env bash
# Vendors schema/ from the intelligence-contract repository at one commit and records its source and checksums
# in schema/CONTRACT-SOURCE.json. ContractVendoringTests fails the build when a vendored file drifts from that record.
# Usage: scripts/vendor-contract.sh <path-to-intelligence-contract> [commit]
set -euo pipefail
contract="${1:?path to the intelligence-contract repository}"
commit="$(git -C "$contract" rev-parse "${2:-HEAD}^{commit}")"
root="$(cd "$(dirname "$0")/.." && pwd)"
if command -v sha256sum >/dev/null; then hash() { sha256sum | cut -d' ' -f1; }; else hash() { shasum -a 256 | cut -d' ' -f1; }; fi

rm -rf "$root/schema"
files=()
while IFS= read -r path; do
  mkdir -p "$root/$(dirname "$path")"
  git -C "$contract" show "$commit:$path" > "$root/$path"
  files+=("$path")
done < <(git -C "$contract" ls-tree -r --name-only "$commit" -- schema | LC_ALL=C sort)

{
  printf '{\n  "repository": "intelligence-contract",\n  "commit": "%s",\n  "files": {\n' "$commit"
  for i in "${!files[@]}"; do
    sep=","; [ "$i" -eq $((${#files[@]} - 1)) ] && sep=""
    printf '    "%s": "%s"%s\n' "${files[$i]}" "$(hash < "$root/${files[$i]}")" "$sep"
  done
  printf '  }\n}\n'
} > "$root/schema/CONTRACT-SOURCE.json"
echo "vendored ${#files[@]} files from $commit"
