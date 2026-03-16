# Managed OpenCode Installation Flow

## Goal

Move OCW to a single, OCW-managed OpenCode installation model that requires no separate user installation, keeps host and container execution on the latest upstream OpenCode release, avoids maintaining multiple host copies, and remains safe when many `ocw run` sessions start concurrently across different profiles.

## Core Decisions

- OCW fully owns OpenCode installation on both host and container sides.
- OCW no longer depends on `opencode` being installed on host `PATH`.
- OCW keeps exactly one managed host OpenCode install under the OCW config root.
- OCW does not support older-version compatibility, version pinning, or fallback execution paths.
- If anything is out of date, OCW updates everything to the latest upstream OpenCode release before launching the session.
- OCW must not require code changes for each OpenCode release; all version resolution comes from upstream machine-readable metadata.
- Profile Dockerfiles stop being responsible for installing OpenCode directly; they provide the environment/tooling layer only.

## Desired End State

After the change:

- `ocw run` resolves the latest upstream OpenCode version.
- OCW ensures the single managed host OpenCode install is updated to that version.
- OCW ensures the Docker runtime image used for the chosen profile is updated to that same version.
- OCW launches `opencode serve` in Docker from that runtime image.
- OCW launches `opencode attach` from the managed host install.
- Concurrent sessions either reuse already-current artifacts or wait safely for shared updates to complete.

## Filesystem Layout

Use the existing OCW host config root `~/.opencode-wrap` and add a managed tools area.

Suggested layout:

```text
~/.opencode-wrap/
  AGENTS.md
  profiles/
  sessions/
  tools/
    opencode/
      current/
        <platform-specific extracted binary layout>
      metadata.json
      leases/
        <session-id>.lease
  locks/
    opencode-latest.lock
    opencode-host.lock
    opencode-runtime-<hash>.lock
```

Notes:

- `current/` is the only retained host OpenCode installation.
- `metadata.json` stores the installed version and artifact metadata for the current host install.
- `leases/` tracks active sessions currently using the host install.
- `locks/` contains inter-process lock files used to coordinate updates across concurrent OCW invocations.

## Version Source of Truth

Use upstream machine-readable metadata rather than hardcoded versions.

Recommended source:

- Query the latest OpenCode release metadata from the upstream release feed/API.
- Extract:
  - latest semantic version
  - per-platform host artifact URL
  - checksums if available

Requirements:

- No OCW source changes for new OpenCode releases.
- Asset resolution must be platform-aware.
- The resolved latest version must be cached briefly to reduce redundant upstream requests during bursts of parallel runs.

## Runtime Image Model

Replace the current implicit "profile image already contains OpenCode" assumption with a two-layer image model.

### Base Profile Image

- Built from the user profile Dockerfile.
- Contains profile tooling, SDKs, shell setup, helpers, and other environment configuration.
- Must not be responsible for installing OpenCode.

### OCW Runtime Image

- Built by OCW on top of the base profile image.
- Installs the resolved latest OpenCode version.
- Is tagged using both:
  - the base profile image identity
  - the resolved latest OpenCode version

This ensures:

- profile changes rebuild the runtime image
- new OpenCode releases rebuild the runtime image
- multiple profiles can independently converge to the same OpenCode version without manual changes

## Concurrency Model

Parallel startup safety is required across many OCW processes.

### Lock Types

#### `opencode-latest.lock`

Protects resolution and refresh of cached upstream "latest version" metadata.

Use it to ensure:

- only one process refreshes latest-version metadata at a time
- all others either reuse fresh cached metadata or wait for the refresh to finish

#### `opencode-host.lock`

Protects the single managed host OpenCode installation and host lease directory.

Use it to ensure:

- only one process updates/replaces the host install at a time
- no process launches a new host attach against a half-updated installation
- updater waits until active host leases drain before replacing the host install

#### `opencode-runtime-<hash>.lock`

One lock per runtime image identity.

Use it to ensure:

- only one process builds a given runtime image
- unrelated profiles/runtime-image combinations can still build in parallel

### Lease Rules

Before launching host `opencode attach`:

- acquire `opencode-host.lock`
- confirm managed host install is current
- create a session lease file in `tools/opencode/leases/`
- release `opencode-host.lock`
- launch attach process

When attach exits:

- reacquire `opencode-host.lock`
- delete the lease
- release `opencode-host.lock`

When an update is needed:

- acquire `opencode-host.lock`
- wait until all lease files are gone
- replace host install atomically
- update metadata
- release `opencode-host.lock`

This keeps the single-copy host installation safe even when many sessions launch at once.

## Host Installation Flow

Create a dedicated service, for example `ManagedHostOpencodeService`, responsible for all host install behavior.

Responsibilities:

- resolve managed install root
- read/write host install metadata
- determine installed host version
- download the correct host artifact for the current OS/arch
- extract/install into a temporary directory
- validate the installed binary by running `--version` and `attach --help`
- atomically swap the temporary directory into `current/`
- expose the exact managed executable path for attach launching

### Host Update Rules

- If no managed host install exists, install latest.
- If managed host install version is older than latest, update it.
- If it already matches latest, reuse it.
- Never keep multiple installed versions after a successful update.
- Never fall back to PATH or legacy installs.

### Atomic Replace Procedure

Under `opencode-host.lock`:

1. Download artifact to a temp path.
2. Extract into a temp install directory.
3. Validate the binary.
4. Wait for all leases to drain.
5. Move existing `current/` aside or delete it.
6. Move temp install into `current/`.
7. Write `metadata.json`.
8. Clean temp paths.

If validation fails, do not modify `current/`.

## Container Installation Flow

Create an OCW-controlled runtime-image build service, for example `OpencodeRuntimeImageService`.

Responsibilities:

- ensure the base profile image exists
- construct a runtime Dockerfile or inline build context that layers latest OpenCode onto the base image
- build/tag the runtime image with a key derived from:
  - base profile image identity
  - latest OpenCode version
- return the runtime image tag for session startup

### Runtime Image Build Strategy

The runtime layer should:

- download/install the resolved latest OpenCode version in a deterministic location
- keep the install step fully controlled by OCW
- avoid requiring profile authors to update their Dockerfiles for each OpenCode release

The preferred build flow is:

1. Resolve latest OpenCode version.
2. Ensure base profile image exists.
3. Build an OCW-generated runtime image `FROM <base-profile-image>`.
4. Install that exact OpenCode version during the runtime-image build.
5. Launch `opencode serve` from that runtime image.

## End-to-End `ocw run` Flow

The final startup sequence should be:

1. Start deferred session logging.
2. Resolve OCW config paths.
3. Acquire/read cached latest OpenCode metadata under `opencode-latest.lock`.
4. Determine the latest upstream OpenCode version and artifact URLs.
5. Under `opencode-host.lock`, ensure the single managed host install is updated to latest.
6. Resolve the selected profile.
7. Ensure the base profile image exists.
8. Under `opencode-runtime-<hash>.lock`, ensure the runtime image for `<base-image, latest-version>` exists.
9. Reserve localhost port and prepare session metadata.
10. Create a host lease for the session under `opencode-host.lock`.
11. Start Docker container running `opencode serve` from the OCW runtime image.
12. Wait for backend readiness.
13. Launch host `opencode attach` using the managed executable path.
14. When attach exits, remove the host lease.
15. Clean up the Docker container and session directory.
16. Flush session logs.

## Failure Handling Rules

- If latest-version resolution fails and there is no current cached metadata, fail fast.
- If host install update fails, do not start the backend.
- If runtime image build fails, do not attempt attach.
- If host binary validation fails, do not replace the current install.
- If backend readiness fails, emit backend logs and clean up.
- If attach startup fails, keep the error inside deferred session logs and still clean up the lease/container.

## Logging Requirements

All update and synchronization actions must flow through deferred session logging.

Log categories should include at least:

- `opencode-version`
- `opencode-host`
- `opencode-runtime`
- `attach`
- `startup`

Important events to log:

- resolved latest version
- cache hit vs cache refresh
- waiting on host/runtime locks
- installing/updating managed host OpenCode
- building/reusing runtime image
- waiting for active host leases to drain
- backend readiness checks
- attach launch path and result

## Migration Plan

### Step 1: Make OCW the only host launcher

- Replace any remaining host PATH assumptions with a managed executable path abstraction.
- Add a host install metadata model.
- Add a managed host install root under the OCW config directory.

### Step 2: Add latest-version resolution and caching

- Implement upstream latest-version resolver.
- Add short-lived cached metadata on disk.
- Add `opencode-latest.lock` to coordinate refresh.

### Step 3: Implement host install/update service

- Add artifact selection by OS/arch.
- Add download/extract/validate/install logic.
- Add atomic replace logic.
- Add `opencode-host.lock` and lease handling.

### Step 4: Separate profile images from OpenCode runtime images

- Refactor Docker image handling so profile images no longer imply bundled OpenCode state.
- Introduce a runtime image builder layered on top of the profile image.
- Include latest OpenCode version in runtime image identity.

### Step 5: Wire `ocw run` to latest-only synchronization

- On every `ocw run`, resolve latest.
- Ensure host managed install is latest.
- Ensure runtime image is latest.
- Launch serve and attach only after both converge.

### Step 6: Remove old assumptions and simplify docs

- Remove documentation saying users must install host `opencode` themselves.
- Update profile guidance so profiles no longer install OpenCode directly.
- Document the single-managed-install and latest-only behavior.

## Implementation Notes

- Use file-based inter-process locks that work cross-platform.
- Keep the host install replacement logic as simple and atomic as possible.
- Keep upstream release resolution encapsulated in one service so future source changes are isolated.
- Keep runtime image generation deterministic so repeated runs are cheap when already up to date.
- Do not add compatibility code for old host/client/backend mixes.

## Acceptance Criteria

The design is complete when all of the following are true:

- A fresh machine can run `ocw run` without a separate OpenCode install.
- OCW maintains only one host OpenCode install copy.
- OCW always updates to latest instead of trying to run older versions.
- Multiple concurrent `ocw run` invocations do not corrupt host installs or runtime images.
- Profile updates and upstream OpenCode releases both trigger the correct rebuild/update behavior.
- No OCW source change is needed when a new OpenCode release appears upstream.
