# AudioStreamer Installer and Updates

**Status:** Frozen v2 deployment specification; RC1 implementation status is tracked in section 11.
**Target:** Machine-wide Windows installation with WinUI UI and a background Windows Service.
**Related:** [Windows Service](WINDOWS_SERVICE.md), [IPC](IPC_PROTOCOL.md), [Test Plan](TEST_PLAN.md).

## 1. Distribution model

Ship a signed machine-wide **MSI with a bootstrapper** for prerequisite checks and installation. The UI is a WinUI 3 desktop application; the service is a .NET 9 Windows Service, `AudioStreamer.Engine` library, IPC contracts, and Windows platform adapters. Installer technology must support Windows Service registration, rollback, repair, and prerequisite chaining. WiX is the current recommendation; exact toolset and signing identity are release engineering decisions.

The installed product has no user-facing batch or PowerShell step. It creates Start Menu shortcuts, optionally a desktop shortcut, and can launch the UI after successful setup. UI launch is optional if no interactive user is logged on.

## 2. Runtime prerequisites and Python decision

The **v2 release target has no Python runtime dependency**. The C#/.NET 9 Engine, Service, WinUI UI, Contracts, and Windows adapters are bundled or installed through supported signed prerequisites. The installer installs the required .NET 9 and Windows App SDK runtimes if the application is not self-contained; choose one deployment mode and test servicing size/security. Windows native audio adapter runtime dependencies must be bundled or declared explicitly.

During migration, the existing Python v1 implementation remains a development/reference build and is not installed alongside the v2 Service. If a temporary migration tool needs Python, it is not part of the user-facing product and never owns production ports or Node ID. Stable v2 must not require Python, Tkinter, a virtual environment, or internet access to install core functionality. The Python reference can remain in source/CI without shipping.

## 3. Installer responsibilities

First install:

1. Check OS architecture/version and prerequisites; display a clear unsupported-system result.
2. Verify Authenticode signatures and package hashes before installation.
3. Install immutable files under Program Files with administrator/service write protection.
4. Create ProgramData and per-user AppData locations with explicit ACLs.
5. Preserve legacy configuration and Node ID for Service bootstrap migration; Service bootstrap invokes the Engine ConfigurationManager migration path once, keeps a protected backup, and reports unsupported fields without logging values. Installer never writes the live runtime settings store directly.
6. Register the AudioStreamer service with automatic/delayed startup policy selected after startup measurements; configure recovery actions and service account.
7. Create Start Menu shortcut and optional desktop shortcut; configure optional per-user launch-at-sign-in for UI only.
8. Start service, verify health/IPC/version handshake, and offer launch UI.

Installer must be idempotent. It must not start duplicate broker/service instances or take ownership of an unrelated process using a configured UDP port.

## 4. Service account, startup, and recovery

Use least privilege proven by the audio/session spike. Service is automatic at boot; delayed automatic start is preferred if it improves system startup without violating node availability requirements. Recovery policy restarts unexpected failures with bounded delays and failure limits. Installer reports service startup failure and keeps a repair path available.

The service process is independent from UI autostart. UI may launch at user sign-in, but node/service startup must not depend on the user running a script or UI. Installer configures pipe ACLs, service SID, data/log ACLs, and firewall guidance through documented OS mechanisms. It does not silently weaken Windows Firewall or open broad inbound access.

## 5. Upgrade transaction

1. Check signed update metadata and minimum supported version; download only signed package endpoints from trusted configuration.
2. Verify manifest signature, publisher identity, package signature, hash, architecture, and version compatibility before staging.
3. Stage a complete service/UI/Engine/adapter set in a versioned staging directory. Do not overwrite active binaries.
4. Ask NodeHost to drain; send GOODBYE best-effort, stop audio lanes, flush logs, and stop service through installer coordination.
5. Atomically switch/install the complete version; update service registration/runtime prerequisites if required.
6. Start service, verify health, IPC schema, compatible Service/Engine assembly set, config migration, and discovery startup.
7. On failure, restore the prior compatible binary set and configuration backup, restart old service, and record a repairable update failure.
8. Restart/open UI only after compatible service handshake. Keep update status visible and preserve logs.

Never let a running service replace its own loaded executable/library. Partial versions are rejected by IPC handshake.

## 6. Versioning and compatibility

Product releases have one semantic version shared by UI, Service, and Engine package. Individual assembly versions may differ but a signed manifest lists exact compatible versions. IPC schema uses independent major/minor negotiation; Engine assembly version is reported separately. Audio Protocol v3 and Control Protocol v1 remain independent and do not change as a side effect of installer versioning.

An update may migrate configuration forward using versioned migrations. Downgrade is supported only if the previous version can consume the migrated config or a pre-migration backup is restored. Unknown fields are preserved where safe; secrets are never copied to logs. Update metadata declares minimum supported prior version and rollback availability.

## 7. Repair and rollback

Repair verifies installed signatures/files, restores missing product files, checks service registration and ACLs, and preserves valid user/machine settings. It must not regenerate Node ID unless the user explicitly chooses reset identity. Rollback restores binaries and compatible config backup as one operation. If rollback cannot start service, leave service disabled/stopped with a repair UI and logs rather than repeatedly crashing.

## 8. Portable mode

Portable mode is **out of scope for the first v2 release**. A service requires machine installation privileges and durable machine storage/ACLs. A future portable package must be an explicit user-session NodeHost mode using the same Engine and protocols, with separate single-instance/resource ownership, user-scoped data, no auto-start service, and no claim to continue after logoff. It may not silently coexist with the installed service on the same ports or Node ID. Do not create a second mode until product requirements and audio behavior are defined.

## 9. Uninstall

Uninstall stops and drains NodeHost, stops/deletes the service, removes immutable binaries/shortcuts, and leaves machine configuration/logs by default with a clear “remove settings and logs” option. It warns if a stream is active and does not promise remote stop delivery. User preferences can be removed per-user. Preserve Node ID/config for reinstall unless the user explicitly opts to erase identity. Failed uninstall records which resource remains and offers repair.

## 10. Release packaging requirements

- Signed MSI/bootstrapper and signed binaries; reproducible build metadata and SBOM.
- No credentials, local config, logs, or development tools in package.
- Offline core installation after prerequisites are included or already present.
- Service recovery and version handshake verified on clean supported Windows images.
- Upgrade, rollback, repair, and uninstall validated from every supported prior release.
- No shipped Python runtime in stable v2; Python remains development/reference-only.

## 11. RC1 implementation status

The repository now contains an x64 WiX v5 machine MSI scaffold at `installer/`, a Windows GitHub Actions build/test/package workflow, an elevated disposable-runner install/IPC/log/recovery validation script, bounded JSONL file logging, process-level crash reporting, one-time import of the supported legacy TOML settings into ProgramData, and `tools/release/Collect-Diagnostics.ps1`. The diagnostics ZIP records OS/service state, configuration presence/schema metadata, and selected log metadata; it omits configuration values, log messages/scopes/property values, peer addresses, and audio data. The file logger omits arbitrary scopes and redacts sensitive structured values when formatting messages. The migration report records imported/unsupported key names only; the original file is retained under `ProgramData\AudioStreamer\MigrationBackups`. `AUDIOSTREAMER_LEGACY_CONFIG` can point to a legacy config when it is outside the service account's LocalAppData or current-directory search locations.

The MSI registers an automatic LocalSystem process, grants it and Administrators access to ProgramData locations, adds a Start Menu shortcut, and configures SCM restart for the first two failures. LocalSystem is required by the current interactive-session AudioAgent launcher, which obtains the active user's token through WTS APIs. Inherited ACLs and backup confidentiality must be verified on supported Windows images. The package currently harvests publish directories and is a packaging scaffold, not yet an approved distribution artifact. Validate its actual UI runtime dependencies, service audio/session behavior, ACLs, upgrade/repair/uninstall paths, and supported Windows matrix before release. The MSI does not yet bundle a prerequisite bootstrapper or install a firewall exception.

`New-UpdateManifest.ps1` creates detached CMS-signed metadata only after checking MSI Authenticode identity. `Test-UpdateManifest.ps1` verifies the pinned signer, detached signature, safe package filename, SHA-256, and package signer. This is release tooling only: there is no in-product downloader, update scheduler, staged transaction, rollback engine, or user-facing update UX. Do not advertise automatic updates.

RC1 is not qualified by CI alone. The workflow must pass on a disposable elevated Windows runner; a physical reference-machine test must still prove audio capture/playback with the LocalSystem service launching the Agent in the interactive user session, cross-device LAN behavior, and the required soak period. Signing secrets, selected license, SBOM generation, and release publication are release-owner inputs and remain explicit gates.

