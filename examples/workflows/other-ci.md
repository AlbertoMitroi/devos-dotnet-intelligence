# Running the image outside GitHub Actions

The Action is a thin wrapper over the image. Any CI system with Docker (or a compatible engine) runs the same analysis:

```bash
docker run --rm \
  --user "$(id -u):$(id -g)" \
  -v "$PWD:/src:ro" \
  -v "$PWD/intelligence-out:/out" \
  -v "$PWD/.dotnet-intelligence/nuget:/cache/nuget" \
  ghcr.io/albertomitroi/dotnet-intelligence:0.2.0 \
  analyze --src /src --out /out
```

- `intelligence-out` must be absent or empty; create it before the run so it belongs to the calling user.
- Exit codes: `0` complete, `10` partial, `20` preflight blocked, `30` nothing could be loaded, `40` cancelled, `50` integrity check failed, `1` internal error, `2` usage error. Decide in the pipeline which ones fail the job.
- Progress events are NDJSON on stdout; logs are on stderr.

GitLab CI, for example:

```yaml
context-artifacts:
  image: docker:27
  services: [docker:27-dind]
  script:
    - mkdir -p intelligence-out
    - docker run --rm -v "$CI_PROJECT_DIR:/src:ro" -v "$CI_PROJECT_DIR/intelligence-out:/out"
        ghcr.io/albertomitroi/dotnet-intelligence:0.2.0 analyze --src /src --out /out || [ $? -eq 10 ]
  artifacts:
    paths: [intelligence-out/]
```
