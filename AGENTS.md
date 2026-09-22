# Project instructions

## Git workflow

- Commit completed, verified work in logical commits unless the user explicitly asks to leave changes uncommitted.
- Keep outstanding hardware validation recorded in the plan; do not describe it as passed merely because the code is committed.

## macOS builds

- Build and package macOS apps for Apple Silicon (`arm64`) only.
- Do not create Intel (`x86_64`) or universal macOS builds unless the user explicitly requests a change to this requirement.
- Verify the packaged app contains only `arm64` before delivering a macOS release.
