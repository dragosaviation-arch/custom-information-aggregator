# CIA licensing, signing and verification

CIA uses the [Attribution Assurance License](https://opensource.org/license/aal) (`AAL`). The root [`LICENSE`](../../LICENSE) contains the approved author-populated license text.

## Signed AAL artifacts and publication gate

The committed `LICENSE.asc` is an ASCII-armored clearsigned copy of the approved `LICENSE`. The matching public key is committed as `docs/licensing/AAL-PUBLIC-KEY.asc`. Its independently confirmed full fingerprint is:

`16C968BD782888D630101761EF8DC38A512D8472`

`CIA.Publish.proj` blocks publish and installer preparation unless both artifacts exist and `deployment/Verify-AalLicense.ps1` confirms all of the following in an isolated temporary public-key store:

- the supplied public key has the approved fingerprint;
- the OpenPGP signature is valid and was made by that key;
- the clearsigned content matches `LICENSE`, allowing only OpenPGP text line-ending canonicalization.

The private key and passphrase must never enter this repository, ChatGPT, CI logs or release artifacts. Any later change to `LICENSE` invalidates the prior signature and requires the owner to sign it again.

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
