# Release checklist

A personal reminder, not a gate anything enforces - nothing here blocks a release if a line gets
skipped. See `.github/workflows/publish-images.yml`/`promote-release.yml` for the pipeline this
supports.

## Before promoting an `edge` build

- [ ] Smoke-tested login and the main nav on the Panel
- [ ] Smoke-tested whatever this release's actual changes touch
- [ ] No unexpected errors in `docker compose logs` for api/panel/worker since the `edge` build went up
- [ ] A real (test-mode) Stripe checkout completes end to end, if this release touches billing
- [ ] Noted the exact commit SHA of the `edge` build actually tested - it's what the release's target
      needs to be set to, not just "whatever main is now"

## Cutting the release

1. GitHub → Releases → **Draft a new release**.
2. Set **Target** to the tested commit from the checklist above, not the `main` branch itself - a merge
   could have landed since you tested.
3. Tag it `vX.Y.Z` ([semver](https://semver.org): MAJOR for a breaking change, MINOR for a
   backward-compatible feature, PATCH for a fix).
4. Write the release notes - see each submodule's own auto-generated notes (its Releases page, or
   `gh api repos/RustArchon/<repo>/releases/generate-notes`) for what actually changed in this range,
   since the umbrella's own PR history is just "bump submodule pointers" and isn't useful to read
   directly.
5. Leave **pre-release** unchecked to promote to `latest` (the normal case - this is what the
   deployment host's `watchtower` actually watches). Check it to tag a version without deploying it.
6. **Publish release.** `promote-release.yml` takes it from there - no rebuild, just the exact image
   already tested getting the new tag(s).
