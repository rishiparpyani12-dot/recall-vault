# Testing and releases

Recall Vault uses three GitHub Actions workflows:

- **CI** builds and tests every pull request and every push to `main`.
- **Native SQLCipher** reproducibly builds and verifies the Windows SQLCipher artifacts when its inputs change or when manually requested.
- **Release** runs the entire test and native-build pipeline, publishes the API and MCP adapter, creates a Windows ZIP and checksum, and optionally creates a GitHub Release.

## Dry-run a release

Open **Actions → Release → Run workflow**, enter a semantic version beginning with `v`, and leave **publish** unchecked. The workflow performs the complete build and uploads a 30-day workflow artifact without creating a tag or GitHub Release.

Set **self_contained** when testers should not need a separately installed .NET runtime. Leave it unchecked for a smaller framework-dependent archive. Tag-triggered releases use the smaller package by default.

The same dry run can be started with GitHub CLI:

```powershell
gh workflow run release.yml --ref main -f version=v0.1.0-beta.1 -f prerelease=true -f publish=false -f self_contained=false
```

## Publish a release

After a dry run passes, rerun it with **publish** checked. The workflow creates the release at the exact commit used by the run. Alternatively, push a version tag such as `v0.1.0-beta.1`; tag-triggered runs publish automatically and treat versions containing `-` as prereleases.

```powershell
gh workflow run release.yml --ref main -f version=v0.1.0-beta.1 -f prerelease=true -f publish=true
```

To enable GitHub artifact attestations, create the repository Actions variable `ENABLE_ATTESTATIONS` with value `true`. On GitHub Free, attestations require a public repository. No custom release token is required; the workflow uses its scoped `GITHUB_TOKEN`.

## Current release status

Release archives remain previews for synthetic or replaceable data. The Windows x64 runtime now includes plaintext migration, documented fail-closed recovery behavior, and the complete key-failure suite. It is not yet a security-complete release for valuable data because independent encrypted backup/key recovery, client credential rotation/revocation, monitoring/restore drills, and final packaging/update security review remain unfinished. See the [security and operations runbook](security-operations.md).
