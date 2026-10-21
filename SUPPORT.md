# CIA Support

Support information for **Custom Information Aggregator (CIA)**, a Windows desktop application for extracting, reviewing and exporting structured XML data.

## Start here

- [User Manual](docs/user-manual/README.md) — the complete workflow, feature reference and troubleshooting.
- [Quick Start](docs/user-manual/quick-start.md) — create your first export.
- [Troubleshooting](docs/user-manual/troubleshooting.md) — common processing and export problems.
- [Known Limitations](docs/user-manual/known-limitations.md) — supported scope and current caveats.
- [Installation Guide](docs/installation/README.md) — installation, upgrades and uninstall.
- [Release Notes / What's New](docs/release-notes/README.md) — version status and release information.

## Report a bug

**GitHub Issues is the intended public support channel for CIA.** Once the repository owner has enabled Issues, use [New issue](https://github.com/dragosaviation-arch/custom-information-aggregator/issues/new/choose) and select **Bug report**. If the Issues page is unavailable, public issue submission has not yet been enabled; follow the documentation and diagnostics guidance below.

Before filing a report, check the current [issues list](https://github.com/dragosaviation-arch/custom-information-aggregator/issues) for an existing report.

Include enough non-sensitive detail to reproduce the behavior:

1. CIA version or release, Windows version and architecture (if known).
2. The affected workspace and sequence of actions, e.g. Load → Discovery → Database → Export.
3. Expected result and observed result, including any exact error message.
4. Source type, approximate number of files and whether **One row per source file** was enabled.
5. Whether the problem reproduces, and any workaround.
6. Relevant **Settings → Log & Diagnostics** entries and redacted screenshots, if safe to share.

CIA works with local files. **Do not post private or proprietary XML, confidential source data, customer/aircraft information, credentials, personal information, complete local file paths, or unredacted logs** in public issues. When necessary, construct a small synthetic XML example that reproduces the problem.

## Suggest an improvement

When Issues is enabled, use [New issue](https://github.com/dragosaviation-arch/custom-information-aggregator/issues/new/choose) and select **Feature request**. Describe the problem or workflow need, desired outcome, example use case and any constraints. Requests are evaluated for future releases; submitting one does not commit CIA to implementing it.

The [roadmap](README.md#release-roadmap) outlines the current post-release direction.

## Find diagnostics

Open **Settings → Log & Diagnostics** to inspect operation results and errors. Check the latest Load/Discovery/Database/Excel operation, the selected source and output configuration, and the visible status message. See the [Troubleshooting chapter](docs/user-manual/troubleshooting.md) before collecting supporting information.

## Scope and expectations

CIA is provided under the [Attribution Assurance License](LICENSE) **as is**, without a guaranteed response time, personalized support contract or warranty. Public support is community/maintainer based. The repository does not provide email-based helpdesk support.

For working with the code or maintaining a fork, see [Forking & Developer Guide](FORKING.md). For the source and latest releases, see the [CIA repository](https://github.com/dragosaviation-arch/custom-information-aggregator) and [GitHub Releases](https://github.com/dragosaviation-arch/custom-information-aggregator/releases).
