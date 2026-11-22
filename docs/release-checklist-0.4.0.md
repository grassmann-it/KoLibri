# Release checklist 0.4.0 (Early Alpha)

Nothing here has been done by automation; the release is published by hand.

- [ ] Merge the remaining PRs into `release/0.4.0`; confirm `Directory.Build.props` says `0.4.0`.
- [ ] Re-point the tag `v0.4.0` to the final commit (`git tag -f v0.4.0 <sha>`, then push the tag). The workflow builds all four archives from that one commit and fails if tag and version differ.
- [ ] Wait for the `Release` workflow: archives `KoLibri-0.4.0-{linux-x64,win-x64,osx-arm64,osx-x64}` and the CLI smoke step must be green.
- [ ] Download the archives; check names and sizes; if the workflow's draft is not used, upload them to the draft release.
- [ ] Validate on Windows: `kolibri --help`, `kolibri-mcp --help`, `kolibri-desktop` starts, `external` folder beside the programs.
- [ ] Validate on macOS (arm64; x64 if available): same checks; allow the unsigned programs under Privacy & Security.
- [ ] Optional on a live account: `kolibri login`, `kolibri status`, one previewed `daily go`.
- [ ] Review the draft notes against `CHANGELOG.md`; mark as pre-release (early alpha).
- [ ] Publish the draft release.
