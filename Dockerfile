# dotnet-intelligence image: build, test and runtime stages (PLAN 2.1).
# Base images are pinned by digest (rule 1.5.4); the SDK set is part of the image identity (D2, D15).
# SDK 10 is the base; SDKs 9 and 8 are copied from their own official images (sdk, shared runtimes, targeting
# packs, workload manifests). The dotnet host of SDK 10 resolves every SDK per global.json (D4).
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
ARG SDK9_IMAGE=mcr.microsoft.com/dotnet/sdk:9.0.320-noble@sha256:9a905addac4d4d3bfdd28185030f874251614559b3a038200849619710b3136b
ARG SDK8_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0.427-noble@sha256:42eb9e70b190d0cca893d93724e67fb89e7570e4309c4b1c20b7f6016a90202d

FROM ${SDK9_IMAGE} AS sdk9
FROM ${SDK8_IMAGE} AS sdk8

FROM ${SDK_IMAGE} AS sdks
COPY --from=sdk9 /usr/share/dotnet/sdk/ /usr/share/dotnet/sdk/
COPY --from=sdk9 /usr/share/dotnet/shared/ /usr/share/dotnet/shared/
COPY --from=sdk9 /usr/share/dotnet/packs/ /usr/share/dotnet/packs/
COPY --from=sdk9 /usr/share/dotnet/sdk-manifests/ /usr/share/dotnet/sdk-manifests/
COPY --from=sdk8 /usr/share/dotnet/sdk/ /usr/share/dotnet/sdk/
COPY --from=sdk8 /usr/share/dotnet/shared/ /usr/share/dotnet/shared/
COPY --from=sdk8 /usr/share/dotnet/packs/ /usr/share/dotnet/packs/
COPY --from=sdk8 /usr/share/dotnet/sdk-manifests/ /usr/share/dotnet/sdk-manifests/

FROM sdks AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props DotnetIntelligence.slnx ./
COPY src/DotnetIntelligence.Contracts/*.csproj src/DotnetIntelligence.Contracts/packages.lock.json src/DotnetIntelligence.Contracts/
COPY src/DotnetIntelligence.Analysis/*.csproj src/DotnetIntelligence.Analysis/packages.lock.json src/DotnetIntelligence.Analysis/
COPY src/DotnetIntelligence.Adapters/*.csproj src/DotnetIntelligence.Adapters/packages.lock.json src/DotnetIntelligence.Adapters/
COPY src/DotnetIntelligence.Artifacts/*.csproj src/DotnetIntelligence.Artifacts/packages.lock.json src/DotnetIntelligence.Artifacts/
COPY src/DotnetIntelligence.Cli/*.csproj src/DotnetIntelligence.Cli/packages.lock.json src/DotnetIntelligence.Cli/
COPY tests/unit/DotnetIntelligence.UnitTests/*.csproj tests/unit/DotnetIntelligence.UnitTests/packages.lock.json tests/unit/DotnetIntelligence.UnitTests/
COPY tests/integration/DotnetIntelligence.IntegrationTests/*.csproj tests/integration/DotnetIntelligence.IntegrationTests/packages.lock.json tests/integration/DotnetIntelligence.IntegrationTests/
RUN dotnet restore DotnetIntelligence.slnx --locked-mode
COPY . .
RUN dotnet build DotnetIntelligence.slnx -c Release --no-restore \
 && dotnet publish src/DotnetIntelligence.Cli/DotnetIntelligence.Cli.csproj -c Release --no-build -o /app

# Unit and integration tests, including the Roslyn rule (R1.4) and spike S1. A failing test fails the build.
FROM build AS test
ENV DI_EXPECTED_SDKS=10.0.403,9.0.320,8.0.427 \
    TESTINGPLATFORM_TELEMETRY_OPTOUT=1
RUN dotnet test --project tests/unit/DotnetIntelligence.UnitTests/DotnetIntelligence.UnitTests.csproj -c Release --no-build --no-progress --output detailed \
 && dotnet test --project tests/integration/DotnetIntelligence.IntegrationTests/DotnetIntelligence.IntegrationTests.csproj -c Release --no-build --no-progress --output detailed

# Runtime: an SDK is required to evaluate analysed projects, so the runtime stage is the SDK image plus the engine.
FROM sdks AS runtime
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_HOME=/tmp/dotnet-home \
    NUGET_PACKAGES=/cache/nuget \
    DI_WORK=/work
COPY --from=build /app /opt/dotnet-intelligence
COPY scripts/fixture-repo.sh /opt/dotnet-intelligence/scripts/fixture-repo.sh
RUN mkdir -p /src /out /cache /config /work \
 && chmod 1777 /out /cache /work
USER app
WORKDIR /work
ENTRYPOINT ["dotnet", "/opt/dotnet-intelligence/dotnet-intelligence.dll"]
CMD ["--help"]

# GitHub Docker container actions must run as root to write GITHUB_WORKSPACE (D16).
# Same layers as runtime; the engine hands its output back to the workspace owner.
FROM runtime AS action
USER root
