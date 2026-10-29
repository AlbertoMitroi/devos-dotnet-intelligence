# Dependencies

Every dependency is pinned (rule 1.5.4). `DependencyPinTests` fails the build when something here is missing or out of date.

## Container images

| Image | Pinned reference | Use | License |
|---|---|---|---|
| .NET 10 SDK | `mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b` | build, test and runtime stages; dependency script | MIT (.NET), Ubuntu packages under their own licenses |
| .NET 9 SDK | `mcr.microsoft.com/dotnet/sdk:9.0.320-noble@sha256:9a905addac4d4d3bfdd28185030f874251614559b3a038200849619710b3136b` | source of `sdk/9.0.320`, runtime 9.0.22, packs and workload manifests copied into the image (D2) | MIT (.NET) |
| .NET 8 SDK | `mcr.microsoft.com/dotnet/sdk:8.0.427-noble@sha256:42eb9e70b190d0cca893d93724e67fb89e7570e4309c4b1c20b7f6016a90202d` | source of `sdk/8.0.427`, runtime 8.0.33, packs and workload manifests copied into the image (D2) | MIT (.NET) |
| actionlint | `docker.io/rhysd/actionlint:1.7.12@sha256:b1934ee5f1c509618f2508e6eb47ee0d3520686341fec936f3b79331f9315667` | CI lint of workflows | MIT |
| ORAS | `ghcr.io/oras-project/oras:v1.3.6@sha256:9cfb578be975b762fd898b9d004243d8003aeb807a3e15b2ed212cdd1326c9eb` | example workflow only (OCI push by the caller) | Apache-2.0 |

## GitHub Actions

Pinned to the commit of the release tag shown in the comment next to each `uses:`.

| Action | Commit | Tag | License |
|---|---|---|---|
| actions/checkout | `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1` | v7.0.1 | MIT |
| actions/upload-artifact | `actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9` | v7.0.2 | MIT |
| actions/download-artifact | `actions/download-artifact@9000827ccba6bdab643e8b6fd33ac0654aef8333` | v8.0.2 | MIT |
| actions/cache | `actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9` | v6.1.0 | MIT |
| docker/setup-buildx-action | `docker/setup-buildx-action@f87e5991a6d7451dcb8d9637bfbc97413f497069` | v4.4.1 | Apache-2.0 |
| docker/login-action | `docker/login-action@81c6f93d4bc991e510b342a00b419b3bb4efc5c6` | v4.7.0 | Apache-2.0 |
| docker/build-push-action | `docker/build-push-action@c3c9e263c25d99ce0380d002d59b67737d91b0dc` | v7.4.0 | Apache-2.0 |

## NuGet packages

Generated from the `packages.lock.json` files and each package's nuspec by `scripts/update-dependencies.sh`.

<!-- nuget:begin -->
| Package | Version | Use | License |
|---|---|---|---|
| Humanizer.Core | 2.14.1 | transitive | MIT |
| Microsoft.ApplicationInsights | 2.23.0 | transitive | MIT |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | transitive | MIT |
| Microsoft.Build.Framework | 17.11.48 | transitive | MIT |
| Microsoft.CodeAnalysis.Analyzers | 5.9.0-1.26328.17 | transitive | MIT |
| Microsoft.CodeAnalysis.Common | 5.9.0 | transitive | MIT |
| Microsoft.CodeAnalysis.CSharp | 5.9.0 | transitive | MIT |
| Microsoft.CodeAnalysis.CSharp.Workspaces | 5.9.0 | direct | MIT |
| Microsoft.CodeAnalysis.Workspaces.Common | 5.9.0 | transitive | MIT |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 5.9.0 | direct | MIT |
| Microsoft.Extensions.DependencyInjection | 10.0.1 | transitive | MIT |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.1 | transitive | MIT |
| Microsoft.Extensions.Logging | 10.0.1 | transitive | MIT |
| Microsoft.Extensions.Logging.Abstractions | 10.0.1 | transitive | MIT |
| Microsoft.Extensions.Options | 10.0.1 | transitive | MIT |
| Microsoft.Extensions.Primitives | 10.0.1 | transitive | MIT |
| Microsoft.Testing.Extensions.Telemetry | 2.5.1 | transitive | MIT |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.5.1 | transitive | MIT |
| Microsoft.Testing.Platform | 2.5.1 | transitive | MIT |
| Microsoft.Testing.Platform.MSBuild | 2.5.1 | transitive | MIT |
| Microsoft.VisualStudio.SolutionPersistence | 1.0.52 | transitive | MIT |
| Microsoft.Win32.Registry | 5.0.0 | transitive | MIT |
| System.Composition | 10.0.1 | transitive | MIT |
| System.Composition.AttributedModel | 10.0.1 | transitive | MIT |
| System.Composition.Convention | 10.0.1 | transitive | MIT |
| System.Composition.Hosting | 10.0.1 | transitive | MIT |
| System.Composition.Runtime | 10.0.1 | transitive | MIT |
| System.Composition.TypedParts | 10.0.1 | transitive | MIT |
| System.Security.AccessControl | 6.0.1 | transitive | MIT |
| xunit.analyzers | 2.2.0 | transitive | Apache-2.0 |
| xunit.v3 | 4.0.2 | direct | Apache-2.0 |
| xunit.v3.assert | 4.0.2 | transitive | Apache-2.0 |
| xunit.v3.common | 4.0.2 | transitive | Apache-2.0 |
| xunit.v3.core.mtp-v2 | 4.0.2 | transitive | Apache-2.0 |
| xunit.v3.extensibility.core | 4.0.2 | transitive | Apache-2.0 |
| xunit.v3.mtp-v2 | 4.0.2 | transitive | Apache-2.0 |
| xunit.v3.runner.common | 4.0.2 | transitive | Apache-2.0 |
| xunit.v3.runner.inproc.console | 4.0.2 | transitive | Apache-2.0 |
| YamlDotNet | 18.1.0 | direct | MIT |
<!-- nuget:end -->
