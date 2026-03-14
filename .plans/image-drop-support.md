# Image Drop Support Plan

## Goal

Allow `ocw` to recognize when the user pastes an image file path from the host terminal, copy that image into a session-scoped staging area under `~/.opencode-wrap`, mount that staging area into the container, and forward the rewritten in-container path to OpenCode instead of the original host path.

This keeps the workflow ergonomic for users while avoiding host/container path mismatches.

## Current Constraint

Today `ocw` launches Docker with a direct interactive `docker run -it ...` call from `OpencodeLauncherService`, so the process does not inspect or rewrite terminal input before it reaches OpenCode. That means this feature needs more than a new mount: it also needs an input relay layer that can detect paste events and rewrite them in flight.

## Implementation Plan

1. Add session-scoped paste staging paths.
   - Add constants in `src/OpencodeWrapConstants.cs` for a host-side session root under `~/.opencode-wrap`, for example `sessions/<session-id>/pastes`, and for the matching container mount root such as `/workspace/.ocw-pastes`.
   - Create a small runtime session model that carries the generated session ID, container name, host paste directory, and container paste directory.
   - Build the session directory before launching Docker so mounts and copy targets are ready.

2. Mount the paste area into the container for every interactive run.
   - In `src/Services/Runtime/OpencodeLauncherService.cs`, create the session paste directory alongside the existing profile resolution and container setup flow.
   - Add a bind mount for the host paste directory to the new container paste root.
   - Keep the mount available for both `ocw run` and direct forwarded `ocw <opencode-args>` launches so the behavior is consistent.

3. Replace the direct passthrough launch with an interactive terminal relay.
   - The current `ProcessRunner.RunAsync("docker", runArgs, captureOutput: false)` call cannot monitor stdin, so refactor the runtime launch path to a dedicated interactive runner.
   - Introduce a terminal relay abstraction that sits between the user's terminal and the Docker process, forwarding output unchanged while inspecting input.
   - Preserve TTY behavior so OpenCode still behaves interactively. On Unix this likely means a PTY-backed relay; on Windows it likely means a ConPTY-backed relay or a platform-specific attached-console implementation.
   - Keep a simple passthrough fallback for non-interactive environments where paste interception is not possible.

4. Detect real paste events instead of guessing from normal typing.
   - Use bracketed paste mode (`ESC[200~` / `ESC[201~`) in the terminal relay so `ocw` only inspects actual pasted chunks.
   - Enable bracketed paste mode when the interactive session starts and restore terminal state when the session ends.
   - If the terminal does not support bracketed paste mode, leave input untouched rather than trying to infer pasted content from timing.

5. Add a pasted-image path rewriter service.
   - Create a focused service such as `PastedImagePathService` under `src/Services/Runtime/` that receives a pasted chunk and decides whether it should be rewritten.
   - Normalize common paste forms before checking them: trim whitespace, unwrap matching quotes, and optionally handle `file://` URIs.
   - Resolve the candidate path against the host working directory when it is relative.
   - Only treat it as a supported image when the file exists and matches the initial image allowlist (`.png`, `.jpg`, `.jpeg`, `.gif`, `.webp`, `.bmp`, and optionally `.svg`). Prefer extension plus a light content sniff for binary formats so accidental rewrites stay rare.
   - If the pasted chunk is not a single supported image path, return it unchanged.

6. Copy matching images into the session staging area and rewrite the text.
   - When a pasted path resolves to a supported image, copy it into the session paste directory using a collision-safe file name strategy such as `<guid>-<sanitized-original-name>`.
   - Preserve the file extension so OpenCode and downstream tooling still infer the correct media type.
   - Return the mounted container path, for example `/workspace/.ocw-pastes/<copied-file-name>`, and forward that rewritten text to the Docker process.
   - Keep the paste area read-only from the container side if possible, since `ocw` is the only process that needs to populate it.

7. Extend cleanup so pasted files disappear when the session closes.
   - On normal exit, delete the session directory in the existing `finally` path in `OpencodeLauncherService` together with any temporary built-in profile directory cleanup.
   - Extend `src/Services/Runtime/ContainerCleanupWatchdog.cs` to also receive the host session directory path and delete it if the parent `ocw` process disappears unexpectedly.
   - Add a small best-effort stale session cleanup pass on startup to remove orphaned session directories left behind by crashes or machine restarts.

8. Keep the first version intentionally narrow.
   - Scope the feature to image files only; do not attempt general host-path translation yet.
   - Only rewrite single-path paste payloads at first; leave multiline pastes, prose, and mixed content untouched.
   - Log or surface a lightweight debug message only when useful, but avoid noisy UX during normal pastes.

9. Verify the behavior with targeted manual checks and focused tests.
   - Add unit coverage for path normalization and image detection if a test project is introduced; otherwise keep the detection logic isolated so it can be tested easily later.
   - Manually verify these cases on at least Linux/macOS and Windows hosts: normal typing unchanged, pasted non-image path unchanged, pasted missing path unchanged, pasted image path rewritten, duplicate image names handled safely, and forced termination cleans up the session paste directory.
   - Confirm OpenCode receives the rewritten container path and can open the copied image successfully from inside the container.

## Suggested File Touch Points

- `src/OpencodeWrapConstants.cs`
- `src/Services/Runtime/OpencodeLauncherService.cs`
- `src/Services/Runtime/ContainerCleanupWatchdog.cs`
- `src/ProcessRunner.cs` or a new dedicated interactive process runner
- new runtime services under `src/Services/Runtime/` for terminal relay and pasted image rewriting

## Key Design Decision

The critical implementation choice is to add a real terminal relay instead of trying to bolt this onto the existing `docker run -it` passthrough. Without that relay, `ocw` has no reliable point where it can detect bracketed paste input, copy host images, and swap host paths for container paths before OpenCode reads them.
