# AGENTS.md

Rules specific to this repository. The general rules (session protocol, decisions and pitfalls,
privacy, verification) come from agent-system's RULE.md and aren't repeated here.

## Commits

- Commit messages follow agent-system D-72 (English, `Why: D-n`).
- Commits use `Cheng Yuan` with the GitHub noreply address, set in this repository's local git
  config.
- The repository is public (D-16). Check every commit for real usernames, home paths and hosts
  before pushing.

## Languages

- Text shown on GitHub is English and tracked files keep their own language (agent-system D-72,
  D-19). In this repository that means:
  - The Actions page counts too: workflow, job, step and artifact names are English. A run is
    titled with its commit subject and the title can't be changed afterwards; a run with a wrong
    title can only be deleted.
  - `docs/`, `windows/README.md`, code comments, script output and self-test logs stay Chinese.
- `README.md` is English and `README.zh-CN.md` is Simplified Chinese; change both together (D-13).
- Every new UI string is written in English, Simplified and Traditional Chinese:
  `L(en, hans, hant)` on the Mac, `L.T(en, hans, hant)` on Windows (D-12).

## Releases

- The version number lives in three places: `Resources/Info.plist`, `windows/EZSnap/EZSnap.csproj`
  and `windows/installer/EZSnap.iss`. Keep them the same (D-15).
- The Mac DMG is built locally with `scripts/package-mac.sh`; the Windows installer comes from the
  CI run for the same commit. Upload both with their SHA-256 files to a GitHub release, published
  as the latest release (D-17). Note anything not tested on a real machine in the release notes.

## Verification

- Mac: `build/EZSnap.app/Contents/MacOS/EZSnap --selftest <dir>` covers drawing and export only.
- Windows can only be run by CI (`.github/workflows/windows.yml`, which includes one real capture)
  and on a real PC; this Mac can build it but not run it.
