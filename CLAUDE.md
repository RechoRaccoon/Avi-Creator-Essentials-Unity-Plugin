# Notes for Claude

## Always push finished work to `main`

The owner wants every finished change pushed to `main`. That's what releases it: `.github/workflows/release.yml` runs on pushes to `main` that change `package.json`, creates the GitHub release and updates the VCC listing so the update button shows up.

- You don't need to ask first. If the session gave you a feature branch, commit there too, then merge it into `main` (a fast-forward when possible) and push `main`.
- Every release bumps `version` in `package.json` and adds a matching `## <version>` section at the top of `CHANGELOG.md`, which becomes the release notes. Without the version bump, nothing is released.
- Update `README.md` when a feature changes how something is used.
- New `.cs` files need a `.meta` file with a fresh GUID, because this is a Unity package.
- The cloud container has no Unity or C# compiler, so code is only compiled when the owner opens Unity after the release. If they report compile errors, fix them and release a patch version.
