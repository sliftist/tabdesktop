Features

AI-Generated Release Notes

- GitHub releases now ship with structured, human-readable release notes describing features and bug fixes, instead of an auto-generated commit list.
- The publish script diffs against the previous release tag and generates the notes via headless Claude Code (`claude -p`), committing them as `RELEASE_NOTES.md` alongside the release.
- If note generation fails, the release proceeds anyway with a commit-list fallback, and older tags without notes still get GitHub's auto-generated notes.
