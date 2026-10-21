# CIA licensing, signing and verification

CIA uses the [Attribution Assurance License](https://opensource.org/license/aal) (`AAL`). The root [`LICENSE`](../../LICENSE) contains the author-populated license text prepared for owner review.

## Current signing gate

The committed `LICENSE` is not yet GPG-signed. It must not be represented as a verifiable signed AAL artifact until the original author supplies both:

- `LICENSE.asc` — an ASCII-armored clearsigned copy of the final, unchanged `LICENSE` text;
- `docs/licensing/AAL-PUBLIC-KEY.asc` — the matching exported public key.

`CIA.Publish.proj` deliberately blocks publish and installer preparation while either signed artifact is absent. The private key and passphrase must never enter this repository, ChatGPT, CI logs or release artifacts.

Before signing, the owner must confirm the copyright year, identity, professional identification, URL and slogan in `LICENSE`. Any later change to `LICENSE` invalidates the prior signature and requires signing again.

## Owner-only signing steps

Run these commands locally from the repository root with GnuPG and the owner's existing private key:

```sh
gpg --armor --clearsign --output LICENSE.asc LICENSE
gpg --verify LICENSE.asc
gpg --armor --export "<YOUR_KEY_FINGERPRINT>" > docs/licensing/AAL-PUBLIC-KEY.asc
gpg --fingerprint "<YOUR_KEY_FINGERPRINT>"
```

Replace `<YOUR_KEY_FINGERPRINT>` with the actual full fingerprint. If no suitable key exists, create and protect one locally before signing; never invent or publish private-key material.

After receiving the public artifacts, a reviewer should:

1. run `gpg --verify LICENSE.asc`;
2. import or inspect `docs/licensing/AAL-PUBLIC-KEY.asc` and compare its full fingerprint with the owner's independently confirmed fingerprint;
3. confirm the clearsigned payload is byte-for-byte the approved `LICENSE` text;
4. build the publication and verify the `licenses` directory contains the signed license, public key, this guide, [`FORKING.md`](../../FORKING.md) and [`THIRD_PARTY_NOTICES.md`](../../THIRD_PARTY_NOTICES.md).

GPG license signing is separate from Windows executable/code signing and does not provide SmartScreen reputation.
