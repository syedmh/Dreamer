# RW4 proxy amendment resolution

Date: 2026-09-04

Status: RESOLVED. ADR-008 and the matching architecture amendment approved
`https://packagefeedproxy.microsoft.io/pypi/simple/` as the sole explicit
release index. The final RW4 self-test passed 12/12: ambient pip configuration
and overrides were disabled, the exact three locked wheels were downloaded
from the explicit proxy, their filenames and hashes matched, and offline
`--no-index --find-links --require-hashes` dry-run, install, and import checks
passed. See `self-test-results.json`.

## Historical direct-PyPI blocker

- `requirements.txt` is the sole release lock and contains exactly one
  `https://pypi.org/simple` index directive, binary-only resolution, and the
  three frozen package versions with one approved CPython 3.13 Windows x86-64
  wheel hash each.
- `validate_release.py` rejects directive, package, version, and hash drift.
- PyPI JSON returned the exact target filenames, byte lengths, and SHA-256
  values recorded in `self-test-results.json`.
- The same three wheel bytes were obtained through the machine's configured
  Microsoft PyPI proxy. Hash-locked local download, mission-local installation,
  import/version smoke, and dry-run all passed.
- A mission-local lock with one changed hash failed with pip exit 1 and the
  expected `THESE PACKAGES DO NOT MATCH THE HASHES` diagnostic.
- README audit and the 74-file protected baseline check passed.

## Required approved-index commands

Both frozen commands were run from the project directory:

```powershell
python -m pip download --require-hashes --only-binary=:all: `
  --dest <fresh-evidence-directory> -r requirements.txt
python -m pip install --dry-run --require-hashes --only-binary=:all: `
  -r requirements.txt
```

Both parsed `requirements.txt` and selected `https://pypi.org/simple`, but
failed while connecting to the PyPI file CDN:

```text
error: ssl-verification-failed
Failed to establish a secure connection to files.pythonhosted.org
[SSL: SSLV3_ALERT_HANDSHAKE_FAILURE] sslv3 alert handshake failure
```

Independent probes reproduced the same environmental failure:

```text
curl.exe: SEC_E_ILLEGAL_MESSAGE during TLS handshake
urllib.request: ssl.SSLError SSLV3_ALERT_HANDSHAKE_FAILURE
all four resolved Fastly IPv4 endpoints: identical handshake failure
```

The machine-wide pip configuration points to
`https://packagefeedproxy.microsoft.io/pypi/simple/`; that mirror can deliver
the exact PyPI-hashed artifacts, but using it for the required proof would
deviate from frozen interface E's sole approved index. No trusted-host bypass,
alternate index, or lock weakening was applied.

## Resolution

Architecture selected the second option: the Microsoft proxy is now the
approved explicit index, not a fallback. `requirements.txt`,
`validate_release.py`, README, and the RW4 evidence all use that exact URL.
The direct `files.pythonhosted.org` failure remains historical context and is
not part of the approved release path.
