# Release checklist 0.4.0 (Early Alpha)

Nothing here has been done by automation; the release is published by hand.

- [ ] Merge the remaining PRs into `release/0.4.0`; confirm `Directory.Build.props` says `0.4.0`.
- [ ] Re-point the tag `v0.4.0` to the final commit (`git tag -f v0.4.0 <sha>`, then push the tag). The workflow builds all four archives from that one commit and fails if tag and version differ.
- [ ] Wait for the `Release` workflow: archives `KoLibri-0.4.0-{linux-x64,win-x64,osx-arm64,osx-x64}` and the CLI smoke step must be green.
- [ ] Download the archives; check names and sizes; if the workflow's draft is not used, upload them to the draft release.
- [ ] Validate on Windows: `kolibri --help`, `kolibri-mcp --help`, `kolibri-desktop` starts, `external` folder beside the programs.
- [ ] Validate on macOS (arm64; x64 if available): same checks; allow the unsigned programs under Privacy & Security.
- [ ] **Required — live proofs on a real account.** Each item needs a recorded proof (date, account, front-end, the
      outcome KoL showed, and a log excerpt or screenshot) linked from the release PR. Offline tests and fakes do not
      count. A failing item blocks the release, or the feature is hidden and the notes say so.
  - [ ] Login and status: `kolibri login`, `kolibri status --refresh`; organ limits match the charpane.
  - [ ] Adventure by hand: one zone visit through the desktop browser tab / encounter view, one fight to its end
        (attack and one skill or item), one choice answered.
  - [ ] Auto-combat batch: the desktop batch over at least 3 turns in one zone; it stops by itself on a choice, a defeat
        or the HP threshold, and the turn count matches the character's adventures used.
  - [ ] Consumables: one use, one eat or drink (or chew); inventory and organ fullness change as expected.
  - [ ] Mall buy: one price-guarded purchase of a cheap item under a price ceiling; the item is in the inventory and the
        receipt is not "unclear".
  - [ ] KMail: send one message (optionally with an item or meat) to your own second account; it arrives.
  - [ ] Experimental extras stay hidden: desktop without arbitrage/clan/farm panels, CLI rejects `arbitrage` without
        `--experimental`, MCP lists only the core tools without `KOLIBRI_EXPERIMENTAL=1`.
- [ ] Review the draft notes against `CHANGELOG.md`; mark as pre-release (early alpha).
- [ ] Publish the draft release.
