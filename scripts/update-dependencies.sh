#!/usr/bin/env bash
# Regenerates the NuGet section of docs/DEPENDENCIES.md from the lock files and package licenses,
# inside the build image (no host SDK). Commit the result.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
image="mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b"
docker run --rm -v "$root:/repo" -w /repo -e DI_UPDATE_DEPENDENCIES=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 "$image" \
  dotnet test --project tests/unit/DotnetIntelligence.UnitTests/DotnetIntelligence.UnitTests.csproj -c Release \
  --filter-method "DotnetIntelligence.UnitTests.DependencyPinTests.Nuget_section_of_dependencies_matches_lock_files_and_licenses"
