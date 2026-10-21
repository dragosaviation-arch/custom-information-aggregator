# Forking CIA — A Word to Future Developers

Hi!

I hope you find CIA useful, and I'd be happy to see somebody expand it into their own use cases. This started as a work requirement and grew into a portfolio project, so I chose the [Attribution Assurance License (AAL)](https://opensource.org/license/aal) to encourage exactly that.

I know AAL can suggest unappealing UI/UX, such as splash screens or pop-ups. Instead, CIA provides an attribution template in the progress-bar area: attribution is shown when CIA is idle, and progress takes its place during operations. The Settings tab also has room for your own developer credentials. You can implement your own approach as long as it respects AAL.

For product endorsement, see my short [standing statement on the repository front page](README.md). You don't need to contact me for permission to create a fork.

Let's keep creating boring, useful software together. I personally enjoyed most seconds of it.

Regards,
**Dragos Tone**

---

## Fork quick start

1. Fork or clone the repository and build the solution following its development instructions.
2. Preserve the original AAL license, the **GPG-signed license text**, the original author's attribution, and applicable third-party notices.
3. Add your own maintainer, project URL and branding using the source locations below.
4. Keep an attribution display that meets AAL's requirements, either by reusing CIA's implementation or by providing a different compliant interface.
5. Test launch, idle, processing, minimum-window layout, packaged license documents and installation before distributing a binary.

**License obligations versus template:** AAL's conditions apply regardless of whether you reuse the original interface. CIA's 200 px status-bar placement, animation, colors and settings layout are **recommended examples**, not terms of AAL. Read the [official AAL text](https://opensource.org/license/aal) before redistribution. An interface design is not a guarantee of legal compliance.

## Customize the status-bar attribution and progress indicator

Repository paths below refer to the CIA v1 implementation. Search for the named controls if the code has subsequently moved.

| Change | File | What to look for |
|---|---|---|
| Change size/layout of the progress/credit slot | [`src/CIA.Desktop/MainWindow.xaml`](src/CIA.Desktop/MainWindow.xaml) | Bottom `Border Grid.Row="3"` / `CiaStatusBarStyle`; `AttributionProgressSlot`, `GlobalProgressPanel` and `OriginalAuthorAttributionButton` |
| Inspect protected original credit or destination | [`src/CIA.Desktop/Presentation/CiaProductIdentity.cs`](src/CIA.Desktop/Presentation/CiaProductIdentity.cs) | `OriginalAuthor`: **Dragos Tone**, professional identification: **Project Manager**, displayed URL: `github.com/dragosaviation-arch`; click opens the [original CIA repository](https://github.com/dragosaviation-arch/custom-information-aggregator). Do not repurpose it as fork-maintainer identity. |
| Customize the fork's product/current developer identity | [`src/CIA.Desktop/Presentation/CiaProductIdentity.cs`](src/CIA.Desktop/Presentation/CiaProductIdentity.cs) | Product constants and `CurrentDeveloper` are the intended fork customization points. |
| Control when progress takes precedence | [`src/CIA.Desktop/Presentation/GlobalStatusViewModel.cs`](src/CIA.Desktop/Presentation/GlobalStatusViewModel.cs) | `IsOperationActive`, `IsProgressVisible`, `IsProgressIndeterminate`, `IsOriginalAttributionVisible`, progress values and operation state |
| Inspect underlying progress computation | [`src/CIA.Desktop/Presentation/GlobalOperationProgress.cs`](src/CIA.Desktop/Presentation/GlobalOperationProgress.cs) | Progress snapshot aggregation |
| Change theme, colors, typography | [`src/CIA.Desktop/Themes/CiaTheme.xaml`](src/CIA.Desktop/Themes/CiaTheme.xaml) | Shared WPF theme resources |

**As shipped in the v1 baseline:** the `ProgressBar` was 150 px wide, and the 38 px percentage indicator occupied the adjacent column. The original `IsProgressVisible` represented **determinate progress only**, not every active operation. The new implementation reserves a 200 px attribution/progress area and must suppress idle attribution for active operations even when no meaningful percentage is available.

**Recommended flow:** Launch → original-author credit; loading/Discovery/Database build/export → progress or a suitable working indicator; inspecting and configuring results → credit again. Attribution should be displayed immediately at application startup, not only after the first operation. A subtle fade may be used, but the attribution must remain legible. The progress indicator must never be obscured by the credit.

The original-credit template uses two compact lines, approximately 12 px semibold and 11 px regular:

```text
Dragos Tone · Project Manager
 github.com/dragosaviation-arch
```

The displayed URL is deliberately compact; the clickable target is the **full original CIA repository**, not the developer's fork or a third-party site. Give your own project its own link in the current-developer section.

When redesigning the layout, keep the required name, professional identification and URL readable (no ellipsis truncating legally required fields). Provide keyboard access and a usable accessible name for the attribution link. Verify the minimum supported window size and relevant Windows display scaling settings.

## Customize developer identity in Settings

The relevant panel is [`src/CIA.Desktop/Views/SettingsWorkspaceView.xaml`](src/CIA.Desktop/Views/SettingsWorkspaceView.xaml). Find `Grid x:Name="AboutHelpPanel"`. It contains three distinct regions:

| Region | Search in XAML | What to customize |
|---|---|---|
| Top application information | `Text="CURRENT DEVELOPER"`, `Text="ORIGINAL AUTHOR"`, `CurrentDeveloperIdentityText` and `OriginalAuthorIdentityText` bindings | Application/fork maintainer details are separate from preserved original authorship |
| Middle picture / branding panel | `ApplicationShortName` and `ApplicationTagline` bindings | Your brand image, product name or tagline |
| Bottom Help & About links | `Text="HELP &amp; ABOUT"` and the `UserManualButton`, `ReleaseNotesButton`, `SupportButton` | Fork's documentation and support destinations |

Product, current-developer and protected original-author identity live in [`src/CIA.Desktop/Presentation/CiaProductIdentity.cs`](src/CIA.Desktop/Presentation/CiaProductIdentity.cs). Settings properties and links are exposed by [`src/CIA.Desktop/Presentation/SettingsWorkspaceViewModel.cs`](src/CIA.Desktop/Presentation/SettingsWorkspaceViewModel.cs): search for `CopyrightText`, `CurrentDeveloperIdentityText`, `OriginalAuthorIdentityText`, `UserManualUri`, `ReleaseNotesUri` and `SupportUri`. Modify `CurrentDeveloper` and product branding for a fork, not the preserved `OriginalAuthor` values.

### Change the picture / branding area

**There is no image file to replace in the original CIA Settings panel.** It is a styled WPF `Border`, in `AboutHelpPanel`, containing two `TextBlock`s: the large `CIA` wordmark and the small `FOCUS · EXTRACT · REVIEW` tagline.

**Option A — text branding:** edit `ApplicationShortName`, `ApplicationName`, `ApplicationMonogram` and `ApplicationTagline` in `CiaProductIdentity.cs`. Adjust the surrounding WPF styling in `SettingsWorkspaceView.xaml` only when the layout also needs to change.

**Option B — actual image:** add a file such as `src/CIA.Desktop/Assets/your-brand.png`, set its WPF **Build Action** to **Resource**, and replace the two-text block inside that panel with an image element, for example:

```xml
<Image Source="pack://application:,,,/Assets/your-brand.png"
       Stretch="Uniform"
       MaxHeight="96"
       AutomationProperties.Name="Your project branding" />
```

Preserve the panel's `Grid.Row="2"` placement and test compact/responsive layout. With a WPF project, verify the image is embedded and visible in the **published/installed** build, not merely in Visual Studio. An embedded resource generally does not need a separate installer file.

**Top-left application icon:** the main window has a separate `C` wordmark in [`src/CIA.Desktop/MainWindow.xaml`](src/CIA.Desktop/MainWindow.xaml). The Settings header uses the `ApplicationMonogram` binding. Update both if you want consistent fork branding. Neither is the original-credit obligation.

**Responsive layout:** the code-behind [`src/CIA.Desktop/Views/SettingsWorkspaceView.xaml.cs`](src/CIA.Desktop/Views/SettingsWorkspaceView.xaml.cs) manages compact Settings presentation through `ApplyResponsiveLayout()`. Keep both ordinary and narrow-window views usable after personalizing the panel.

## What stays from the original CIA project?

- Preserve the signed AAL license text and its verifiable signature in source/binary distributions as required by AAL.
- Preserve **Dragos Tone — Project Manager — github.com/dragosaviation-arch** as the original-author attribution in an AAL-compliant launch display. The provided status-bar design is a template; another compliant design is allowed.
- Your name, project title, fork version, UI art and links may be added separately, including in the developer area in Settings. This is not a claim that the original author built or reviewed your changes.
- Keep third-party license notices associated with the libraries you actually redistribute.
- The short [standing endorsement on the CIA README](README.md) allows a defined general endorsement statement; it is not certification or individual review of a derivative product.

AAL uses a **GPG-signed license text**. Signatures help verify the original text and signer; do not edit an existing signed document or publish private signing keys. Refer to the repository's [license signing and verification instructions](docs/licensing/README.md) and the signed artifact shipped with the version you forked.

## Test before distributing your fork

| Test | Expected result |
|---|---|
| Launch | Prominent original attribution is readable from startup; no splash/pop-up required by the CIA template |
| Start processing | Progress/working status replaces idle attribution without overlap |
| Finish/cancel/fail processing | UI returns to an appropriate non-processing state and restores idle attribution |
| Narrow window / scaled display | Full required identification remains readable and operable |
| Click attribution | Opens the original CIA repository; other fork links go to their own destinations |
| Open Settings | Both original author and your current-developer details are correctly distinguished |
| Branding graphic | New text/image fits and renders in an actual installed build |
| Redistribute | Original signed license, public verification material and required third-party notices are present |

Relevant tests include `tests/CIA.Desktop.Tests/GlobalStatusViewModelTests.cs`, `ApplicationStartupSmokeTests.cs`, `SettingsWorkspaceV03Tests.cs`, `UiUxBatch1Tests.cs` and the licensing/package checks. Additional focused attribution and resource-packaging tests may be necessary after a fork changes these surfaces.

## Packaging locations

- `CIA.Publish.proj` — collects the shipped application files.
- `CIA.Installer.proj` — builds the installer.
- `deployment/nsis/CIA.Installer.nsi` — packages the publish directory using `File /r "${CIA_PUBLISH_DIR}\*"`.

`CIA.Publish.proj` validates and copies the applicable license, verifiable signed text, public key/verification guidance, forking guide and third-party notices before NSIS collects the publish directory. Publication is intentionally blocked when the signed license or public key is absent. Recheck that gate and the resulting `licenses` directory if your fork changes packaging.

For CIA product help, see the [User Manual](docs/user-manual/README.md); for the original source, see the [CIA repository](https://github.com/dragosaviation-arch/custom-information-aggregator).
