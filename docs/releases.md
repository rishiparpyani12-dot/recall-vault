# Testing and releases

Recall Vault uses three GitHub Actions workflows:

- **CI** builds and tests every pull request and every push to `main`.
- **Native SQLCipher** reproducibly builds and verifies the Windows SQLCipher artifacts when its inputs change or when manually requested.
- **Release** runs the entire test and native-build pipeline, publishes the API and MCP adapter, creates a Windows ZIP and checksum, and optionally creates a GitHub Release.

## Dry-run a release

Open **Actions → Release → Run workflow**, enter a semantic version beginning with `v`, and leave **publish** unchecked. The workflow performs the complete build and uploads a 30-day workflow artifact without creating a tag or GitHub Release.

The workflow defaults to a self-contained package so the preview server does not need a separately installed .NET runtime. Tag-triggered releases are also self-contained. Turn **self_contained** off only for an intentional framework-dependent test artifact that will not be consumed by the preview updater.

The same dry run can be started with GitHub CLI:

```powershell
gh workflow run release.yml --ref main -f version=v0.1.0-beta.1 -f prerelease=true -f publish=false -f self_contained=true
```

## Publish a release

After a dry run passes, rerun it with **publish** checked. The workflow creates the release at the exact commit used by the run. Alternatively, push a version tag such as `v0.1.0-beta.1`; tag-triggered runs publish automatically and treat versions containing `-` as prereleases.

```powershell
gh workflow run release.yml --ref main -f version=v0.1.0-beta.1 -f prerelease=true -f publish=true
```

To enable GitHub artifact attestations, create the repository Actions variable `ENABLE_ATTESTATIONS` with value `true`. On GitHub Free, attestations require a public repository. No custom release token is required; the workflow uses its scoped `GITHUB_TOKEN`.

## Stage a prerelease on the Windows preview server

Every release includes `release-manifest.json`, which pins the repository, version, source commit, archive name, self-contained mode, deployment classification, and SHA-256 digest. The public preview server uses a pull-based updater instead of a persistent self-hosted GitHub Actions runner.

Copy `deploy/preview/windows/sync-preview-release.ps1` to an access-controlled updater directory on the server and run it as a low-privilege deployment account:

```powershell
.\sync-preview-release.ps1
```

The updater considers only published, non-draft prereleases from `rishiparpyani12-dot/recall-vault`. It validates the manifest and archive digest, extracts into a versioned directory, and writes `C:\RecallVault\pending-release.json`. It does not activate the package.

After inspecting the staged version and commit, explicitly activate that exact version from a private PowerShell session containing the required secret-backed environment variables:

```powershell
& C:\RecallVault\releases\v0.1.0-beta.1\deploy\activate-staged-preview.ps1 -ConfirmVersion v0.1.0-beta.1
```

Activation requires an explicit `Recall__DataDirectory` beneath `C:\RecallVault`, stops only the recorded Recall process IDs, takes a stopped-data backup, starts the package on loopback, and runs API/MCP health checks. If activation fails, it restores the data backup and attempts to restart the previous known-good package. Keep automatic polling in stage-only mode; publishing a prerelease must never bypass explicit local activation during the private preview.

The checksum protects against corruption and unintentional replacement but shares the GitHub release trust boundary with the archive. Protect release publication with phishing-resistant MFA, reviewed commits, minimal workflow permissions, and artifact attestations where available.

## Current release status

Release archives remain previews for synthetic or replaceable data. The Windows x64 runtime now includes plaintext migration, documented fail-closed recovery behavior, and the complete key-failure suite. It is not yet a security-complete release for valuable data because independent encrypted backup/key recovery, client credential rotation/revocation, monitoring/restore drills, and final packaging/update security review remain unfinished. See the [security and operations runbook](security-operations.md).
