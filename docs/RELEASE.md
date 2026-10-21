# Releasing AgentControl

A release is two commands: `build.ps1` makes it, `release.ps1` publishes it. Everything published (the public
repository, the GitHub Release, the website and the release notes) comes from one file: the privacy-scanned source zip
in `dist\<version>\`. That way the copies can't drift apart. The design follows the DarkCipher release standard, with
the same steps, kill switch and gates as MaskedMail.

## Repositories

| Repository | What it holds | Visibility |
|---|---|---|
| `DarkCipher0x/AgentControl-private` | The full working source: this folder's git history, including maintainer notes. | Private |
| `DarkCipher0x/AgentControl` | The sanitised public source: one commit and one tag `v<version>` per release, made only by `release.ps1`. | Public |

The public tree is replaced wholesale on every release. Edit the README and other files here, never on GitHub.

## Cutting a release

1. Bump `<Version>` in `Directory.Build.props`. Every build gets a new version; `build.ps1` refuses to run if
   `dist\<version>\` exists, and old `dist` folders are never deleted.
2. Write `docs/release-notes/<version>.md`, starting `# DarkCipher AgentControl <version> — release notes (<date>)`,
   and add a CHANGELOG entry.
3. Build:

   ```powershell
   powershell -ExecutionPolicy Bypass -File build.ps1
   ```

   It builds, tests and packages everything (details below) and writes `dist\<version>\`. That folder includes
   `DarkCipherAgentControl-<version>-source.zip`, the public source.
4. If the build lists an image that isn't approved yet, look at it, then add its SHA-256 to `tools\approved-images.txt`.
5. Check what publishing would do, then publish:

   ```powershell
   powershell -ExecutionPolicy Bypass -File release.ps1 -DryRun
   powershell -ExecutionPolicy Bypass -File release.ps1
   ```

### What build.ps1 does

1. Locked restore and the local WiX 5.0.2 tool restore.
2. Release build.
3. Full test suite.
4. NuGet vulnerability scan; any advisory fails the build.
5. Headless self-test.
6. Self-contained publish, giving the portable zip.
7. Per-machine MSI with ICE validation, then an **administrative extract** (nothing is installed). The extract must
   hold the same files with an identical exe hash, and the self-test runs against the extracted exe.
8. SDK nupkg, the `dcx-ext` zip and the four sample `.dcxext` packages, each packed and re-verified.
9. CycloneDX SBOM and licence list; docs zip, `RELEASE-NOTES.md`, `BUILD-INFO.txt`.
10. The **public source zip** (`tools\release\write-source.ps1`, gates below).
11. `SHA256SUMS.txt` over every file, every line re-verified from disk, and the portable zip compared with the publish folder.

## The steps of release.ps1

The steps always run in this order, and a step never starts before the earlier ones are complete. What happened is
recorded in `dist\<version>\release-state.json`.

| Step | What it does | Public? | Already done when… |
|---|---|---|---|
| **verify** | Every expected file is in `dist\<version>\`. `SHA256SUMS.txt` verifies and covers every file. The MSI, `BUILD-INFO.txt`, the source's `Directory.Build.props` and the release notes all say the same version. Every test passed, and both self-tests (release build and MSI payload) report 0 failures. | No | (only reads; always runs) |
| **sanitise** | Deletes and recreates `%TEMP%\dcac-public-<version>\` from the source zip, then checks every file again: allowlist, no key or package files, every image approved, privacy scan, gitleaks. | No | (always rebuilt from scratch) |
| **mirror** | Checks that the clone `..\AgentControl-public` has exactly the expected `origin`, is on `main` and is clean. Makes its tree the staged copy: one commit `AgentControl <version> - sanitised public source` and an annotated tag `v<version>`, as DarkCipher0x with the no-reply address. Pushes both, **never with `--force`**. | Yes | `v<version>` is on GitHub with exactly this content. With different content it stops: a published version is never changed. |
| **release** | The GitHub Release `AgentControl <version>`: notes from the public copy's release notes, plus every file in `dist\<version>\`. GitHub's SHA-256 of every file must equal ours. Then it writes `release-manifest.json` for the website. | Yes | Every file is there with a matching SHA-256. A missing file is uploaded; a different one stops the step. |
| **website** | Downloads every file in `release-manifest.json` from darkciphersystems.com and compares SHA-256s. It never changes the site. | Yes | Every file is live and identical. Not live yet: `pending`. Different: `failed` (a failed publish). |
| **discord** | Checks the #agentcontrol card was posted (`%USERPROFILE%\.darkcipher\discord-posted.json`). | Yes | Recorded as posted; otherwise `pending`. |

## Kill switch

The public steps (mirror, release, website, discord) can be stopped at any time without editing anything. The same
variable stops every DarkCipher app:

```powershell
setx DCX_PUBLISH_OFF 1
```

Every new shell then holds them (`$env:DCX_PUBLISH_OFF = '1'` holds them in the current one). The local steps still run,
and the record says `held`. To lift it, `setx DCX_PUBLISH_OFF 0`, then run `release.ps1` again. `-NoPublish` holds
the public steps for one run.

## When something fails

Nothing that already succeeded is undone. The failed step is recorded as `failed` with the reason, and the run stops.
Fix the cause and run `release.ps1` again: finished steps recognise their own work and skip it. `-From <step>`
carries on from a step, once every earlier step is recorded as complete for that version.

| Failed step | State it leaves | To resume |
|---|---|---|
| verify | Nothing published | Fix `dist\<version>\` (a new build means a new version) |
| sanitise | Nothing published; the reason is listed per file | Fix the source and build a new version, or approve the images it lists |
| mirror | If the push failed, the commit and tag wait in the clone | Run again: it pushes them |
| release | A release with the files uploaded so far | Run again: it uploads the rest |

## What is published

- **`tools\public-allowlist.txt`** lists every file that may be published, by pattern. **`tools\private-list.txt`**
  lists those that never are: `RESUME.md`, `docs\specs\`, the local term list, the gitleaks binary and signing
  material. A file in neither stops the build until it is classified, so nothing is published or dropped without a
  decision. Build output and local caches are never considered, and `dist\` never goes into git.
- **Scans**, run by `build.ps1` and again by the sanitise step:
  - local user paths, private e-mail addresses and the Windows user name;
  - API keys and tokens, Discord webhooks and private keys;
  - the private names in `tools\privacy-terms.local.txt` (git-ignored, never published);
  - **gitleaks 8.30.1** with its own rules plus `tools\gitleaks.toml`. `tools\release\get-gitleaks.ps1` installs it
    once into `tools\bin\`, after checking the download's SHA-256 against the published one. The two synthetic test
    values the redaction tests use are allowed by exact value, never by path.
- **Images:** every image must be listed by SHA-256 in `tools\approved-images.txt`, which means someone has looked at
  it. Scans can't see pixels.

## Website and Discord

The website is built and deployed by the **Website & Deployments** session, never by this project. Every production
deploy needs the owner's go. This project hands over and then checks:

- **The manifest.** The release step writes `dist\<version>\release-manifest.json`. It lists every file the site
  publishes (installer, portable, documentation, source) with its path on the site, size, SHA-256 and the GitHub
  Release asset it must equal.
- **The check.** `release.ps1 -From website` downloads each file from the live site and compares it byte for byte.
  Run it after the deploy: any difference is a failed publish.
- **Discord.** The card is posted to #agentcontrol by the website's `announce-discord.ps1`, after a preview and the
  owner's OK. The webhook is kept in Windows Credential Manager (`DarkCipher/Discord/agentcontrol`), never in a file.
  Each post is recorded, so it is never sent twice. `release.ps1 -From discord` then records it here.

## Rolling back

A published version is never edited or deleted to "fix" it, because its checksums are already public. Release a new
version instead. If something must come down at once:

1. `setx DCX_PUBLISH_OFF 1`, so nothing else goes out.
2. **GitHub Release:** mark it as a pre-release, or delete it on GitHub.
3. **Repository:** the next release's commit replaces the files. Removing a pushed commit needs `git push --force`,
   which is never done without the owner's explicit go.
4. **Website:** the Website & Deployments session restores the previous version from its release snapshot.
5. **Discord:** delete the card in #agentcontrol, and post a correction if people may have downloaded the bad version.

## Signing

**Not available.** No code-signing certificate exists, so the MSI, exe and packages are unsigned. For the same reason
the updater has no release key and refuses every update (it fails closed). When a certificate exists:

1. Sign the exe, DLLs and MSI (Authenticode).
2. Create an ECDSA P-256 release key and embed the public key in `Bootstrapper` (`UpdateSecurityService`).
3. Sign `update.json` with `UpdateSecurityService.CanonicalPayload`.
