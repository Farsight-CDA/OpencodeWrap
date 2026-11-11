# Dependency upgrade review — 2026-11-11

## Versions reviewed and applied

| Dependency | Previous | Updated | Current NuGet owners |
| --- | --- | --- | --- |
| CliWrap | 3.10.0 | 3.10.5 | Tyrrrz |
| Farsight.Common | 0.9.5-beta | 1.2.0 | Playwo |
| FluentValidation | 12.1.1 (transitive) | 12.2.0 (explicit reference) | jskinner |
| Spectre.Console | 0.54.0 | 0.57.2 | spectreconsole, spectresystems |
| Spectre.Console.Ansi | Not previously present | 0.57.2 (transitive) | spectreconsole, spectresystems |
| System.CommandLine | 2.0.3 | 3.0.0 | Microsoft, dotnetframework |

CI's Node version changes from 20 to **26.13.0**, the current Node 26 LTS release at review time. The explicit FluentValidation reference selects 12.2.0 rather than Farsight.Common's lower minimum version.

## Supply-chain checks

- Downloaded old and new NuGet archives directly from `api.nuget.org`. The configured restore source was `https://api.nuget.org/v3/index.json`.
- Verified every downloaded old/new package with `dotnet nuget verify --all`; all passed. All have NuGet repository signatures. System.CommandLine additionally has a Microsoft author signature; its certificate changed between versions, and the new signature verified successfully.
- Compared the NuGet owners displayed on the old/new version pages against the expected maintainers. These are **current ownership listings**, not historical proof that ownership never changed.
- Inspected archive file lists, nuspec metadata, repository URLs/commits, dependencies, build targets, and analyzers. Compared the restored archives in the NuGet cache against the reviewed archives using SHA-256; all six application packages matched.
- Downloaded source snapshots at the package-declared repository commits for CliWrap, Farsight.Common, FluentValidation, and Spectre.Console. Performed focused source/diff review and searches for network calls, process execution, environment access, native imports, filesystem access, and initialization hooks. Inspected managed assembly metadata without loading the package assemblies.
- Retrieved System.CommandLine source at upstream tag `v3.0.0`; its packaged MSBuild targets matched that source. Its nuspec records the Microsoft `dotnet/dotnet` aggregate repository, so this tag comparison is not a reproducible binary-to-source verification.
- Ran the NuGet vulnerability check with `--include-transitive`; it reported no known vulnerabilities in the resolved graph at review time.

### Findings requiring closer inspection

**Farsight.Common:** Still ships `analyzers/dotnet/cs/Farsight.Common.Generator.dll`, which executes during compilation. Reviewed generator changes concern service discovery/registration, dependency injection diagnostics, and configuration validation. The packaged analyzer also contains polyfill utilities with process, socket, and filesystem references. Those references and native imports already existed in 0.9.5-beta; the newly added type references were Roslyn syntax/symbol APIs. Neither analyzer had a module initializer. Inspected generated application code; the generated registration initializer performs DI registration, with no network/process hooks found.

**System.CommandLine:** Adds `build/System.CommandLine.targets` and `buildTransitive/System.CommandLine.targets`. The reviewed targets add runtime executable-name metadata and an optional assembly attribute; no `Exec`, custom build task, or download hook was present. Response-file parsing reads caller-specified files as expected.

**CliWrap:** 3.10.5 internalizes PowerKit 2.3.1 into its DLL, so the embedded helper code is absent from the resolved runtime dependency list. Downloaded and signature-verified PowerKit separately and reviewed its source at commit `f64cd6d1c5ce82fe27b329dbcdf9b6a55dcf0467`. Its HTTP, environment, and process helpers are caller-invoked utilities; no hard-coded outbound endpoint or module initializer was found in the inspected material. Reviewed CliWrap's Windows signaler source and deployment code: it extracts an embedded executable and launches it without a shell to send console signals. This mechanism predates the upgrade.

**Spectre.Console:** Adds Spectre.Console.Ansi from the same project and current NuGet owners. Reviewed terminal detection/environment access and Windows `kernel32.dll` imports; these implement console handle/mode detection. Neither console package adds consumer build targets or analyzers.

**Node:** Verified the official 26.13.0 release manifest's detached GPG signature using the Node project's release keyring. Signer fingerprint: `C82FA3AE1CBEDC6BE46B9360C43CEC45C17AB93C` (Richard Lau), also listed in the release's upstream README. Downloaded the Linux x64 archive and matched its SHA-256 against that signed manifest. CI uses the exact reviewed version instead of a floating major.

**npm publishing:** Reviewed the wrapper, package manifests, publishing workflow, and retry script. The reviewed package manifests have no lifecycle scripts. The wrapper uses Node built-ins and resolves the OCW platform package before spawning the binary. Local packaging/install checks used offline tarballs and disabled scripts. Existing GitHub Actions references use mutable version tags; reviewing these dependency artifacts does not pin or independently audit the action implementations.

## Reviewed artifact hashes

SHA-256 of downloaded archives:

| Artifact | SHA-256 |
| --- | --- |
| CliWrap 3.10.5 | `2961d1afd77c2a06ba60d0c2894e93ef507d75214881bcd2e7586ec69ac752c6` |
| Farsight.Common 1.2.0 | `7804f27f64d062aa63630ba5e3640a55f4651ee499afd7db90c7ad9bc69aee5b` |
| FluentValidation 12.2.0 | `872b42a2181f565e2d1beadfdf20b6ccb06b5b473980ad1200f44e4dbeeec6a5` |
| Spectre.Console 0.57.2 | `3912dae8d5646ac922d1760fe0c83e06dc4eee4b8864d38e1851b5b891ebc30c` |
| Spectre.Console.Ansi 0.57.2 | `a4bf214836d6d02cbd8e9afd1cab1f50fcd8d56bd63fb974567770a711d68c2c` |
| System.CommandLine 3.0.0 | `214c8faaab3dbe6009d60d34cd7a093f758cf2a16228e00bc29feeedd108fd41` |
| PowerKit 2.3.1 (embedded upstream helper) | `4359dae334952efb3ce973309e7757225943b30b7c73eb8b36e72053ebef0b40` |
| node-v26.13.0-linux-x64.tar.xz | `d28a96af6c38596f6d9d97c0ee531c13d238d757742387d1928de822d959e997` |

## Compatibility verification

- Release build and Linux Native AOT publish passed with the upgraded dependencies, with no reported warnings or errors.
- Native executable smoke checks passed: root and subcommand help, profile/addon creation and listing in a scratch HOME, missing arguments, invalid profile names, and unknown options.
- Focused checks using OCW's actual `ProcessRunner.cs` passed: argument boundaries and literal shell metacharacters, concurrent 200 KB stdout/stderr capture, nonzero exit status, working directory/environment overrides, missing executable, and CliWrap cancellation. FluentValidation valid/invalid flags-enum checks also passed.
- Under the checksum-verified Node release, npm packing, offline installation, and wrapper-to-binary dispatch passed. These Linux checks were completed before the concurrent Windows-only conversion and provide local compatibility evidence.
- Windows dependency restore passed. A Release `win-x64` build of a scratch snapshot of the pre-conversion repository plus these dependency upgrades passed with zero warnings/errors. Windows Native AOT publishing and execution require the Windows CI runner.
- Workflow YAML parsing and embedded Bash syntax checks passed. The concurrent Windows-only conversion may subsequently change the workflow and application code; its final integrated build must be verified with those changes.

## Conclusion and scope

No indicators of compromise were found in these focused checks. Valid signatures establish archive integrity and signing identity, not that a publisher's code is harmless. This was not an exhaustive source audit or reproducible-build comparison of all shipped binaries. Raw review artifacts, source snapshots, and check output were retained for this disposable session under `/tmp/opencode/dependency-review/`.
