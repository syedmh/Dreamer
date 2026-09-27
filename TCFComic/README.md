# TCFComic

TCFComic is a Python 3.11+ Windows command-line application that watches a local
folder or processes one image through a configured image-edit provider. The
default instruction requests a Studio Ghibli-inspired hand-painted Japanese
animation aesthetic while retaining the recognizable subject and major
composition. This wording describes an aesthetic only; TCFComic does not claim
endorsement by or affiliation with Studio Ghibli.

The MVP supports JPEG/JPG, PNG, and WebP inputs, always publishes validated PNG
outputs, preserves source bytes, and includes a deterministic offline fake
provider for tests and local workflow checks.

JPEG-based MPO (multiple-photo) containers are supported **only when the original
filename ends in `.jpg` or `.jpeg`**, case-insensitively. Only primary frame 0 is
fully decoded and sent; extra frames are ignored. Public OpenAI and Azure convert
that primary frame in memory to a single RGB/RGBA PNG, apply EXIF orientation, and
strip metadata (including EXIF/GPS, comments, and XMP). Encoding is bounded by the
existing `limits.max_input_bytes`; dimensions, oriented dimensions, pixels, and
decompression limits still apply. There is no resizing, lossy fallback, or limit
increase. Source and staged bytes/hashes remain unchanged. Animated PNG, WebP,
and GIF remain unsupported; output validation still requires a single-frame PNG.

## Requirements

- 64-bit Windows 10 or Windows 11
- Python 3.11 or later
- Local source and destination directories; UNC paths are not supported

The project metadata is compatible with Python 3.11 and later. The rework
validation documented here was executed on Python 3.13.15; Python 3.11 was not
available in that environment. No application tests were executed under Python
3.11; only dependency resolution compatibility was checked for Windows CPython
3.11.

## Install in Windows PowerShell

```powershell
Set-Location C:\Users\syedhu\source\repos\Dreamer\TCFComic
py -3.13 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install -c constraints.txt -e ".[dev]"
python -m pip check
python -m pip_audit
```

If another supported Python is installed, select it explicitly when creating
the virtual environment. Direct production and development dependencies remain
exactly constrained in `pyproject.toml`; `constraints.txt` adds the verified
runtime, development, audit, and build transitive versions. It is a portable,
non-hashed constraints strategy rather than a bit-for-bit wheel lock, so release
verification still includes `pip check`, `pip_audit`, and the supported-runtime
resolution check documented below.

## Configure

Copy the example and edit only local, absolute Windows paths:

```powershell
Copy-Item .\config.example.yaml .\config.yaml
New-Item -ItemType Directory -Force C:\Images\Incoming | Out-Null
New-Item -ItemType Directory -Force C:\Images\Transformed | Out-Null
```

Configuration is parsed with `yaml.safe_load` and an exact schema. Unknown
fields, type mismatches, recursive mode, missing paths, overlapping source and
destination trees, remote paths, and quarantine paths outside the destination
are rejected.

Provider credentials are never accepted in YAML or on the command line. For
OpenAI, set the sole supported credential environment variable in the current
PowerShell session:

```powershell
$env:OPENAI_API_KEY = "your-key-from-a-secure-secret-store"
```

Do not put credentials in `config.yaml`, scripts, logs, or committed files.

To run entirely offline, set:

```yaml
provider:
  name: fake
  model: gpt-image-2
  prompt: >-
    Transform this picture into a Studio Ghibli-inspired hand-painted
    Japanese animation aesthetic while retaining the recognizable subject
    and major composition.
  request_timeout_seconds: 120
```

The fake provider requires no key and creates a deterministic valid PNG. It
does not make a network request, and image bytes and prompts remain local.

### Keep published PNGs at their recorded output names

The output directory is the ledger's publication location, not a disposable
export folder. Moving a successful PNG elsewhere (including an `output\Backup`
subfolder) makes its recorded output missing. Startup or duplicate admission
then marks that job as a local publication-integrity failure, not an Azure
failure. **Keep archive copies instead of moving the published originals.**

To repair this failure, restore a copy of the exact original PNG at its original
recorded output name. Do not rename another image to substitute for it. Startup
or admission of the same source/variant automatically verifies the output's
SHA-256 and single-frame PNG validity under current limits, matches the original
recovery provenance and complete successful attempt history, and reconciles the
same job to success without a provider request or a new attempt. The original
failure record is retained with immutable audit copies and a prepared restoration
marker; a marker alone is not proof that the guarded state update committed.
Missing, changed, unsafe, or unproven files remain failed with an explanation.
The application does not search backup directories or restore files itself.

`--retry-failed-variants` is for eligible, verified provider-response failures,
**not missing publications or moderation refusals**. Restored publications take
the local verification path even with this flag. Neither restoration nor retries
guarantee an output for every configured variant.

### Multiple prompts and explicit state reset

Use either `provider.prompt` (legacy, one output) or `provider.prompts`, never both:

```yaml
provider:
  name: fake
  model: gpt-image-2
  prompts:
    tcf-school: "Create a hand-painted school portrait."
    pakistani-80s: "Create a realistic 1980s Pakistani fashion portrait."
  requests_per_minute: 2
```

Names are unique, lowercase ASCII slugs of at most 32 characters, starting with
a letter and containing letters, digits, and single separating hyphens.
Empty mappings/text, duplicate YAML keys, path separators, and device names are
rejected. Order is preserved. Each original is independently staged for each
prompt; the second request never uses the first output. Named output filenames
include the prompt slug. All variants share one queue, one destination lock,
and the same global request/429 pacing. One failure does not replay another
variant's success. Adding a name schedules only that new slot; changing an
existing name's text does not rerender completed slots and fails closed for
pending work whose full request identity changed. Removed pending variants
are never reassigned to another prompt.

`process` handles every configured variant of only the requested original and
prints one successful output path per stdout line. Any variant failure yields
a nonzero exit; independent remaining variants still run unless authentication
fails or shutdown is requested. `--retry-input-rejection` applies separately
to eligible never-sent MPO rejections, retaining all existing audit safeguards.

### Fallback prompt variants

`provider.fallbacks` optionally maps a primary prompt name to a fallback prompt
name. A fallback runs for a source version **only when its primary failed
because the provider rejected the request**, for example Azure
`moderation_blocked` on a photorealistic prompt that a painterly prompt passes:

```yaml
provider:
  prompts:
    pakistani-80s: "..."
    pakistani-cinematic: "..."
    pakistani-80s-painted: "..."
    pakistani-cinematic-painted: "..."
  fallbacks:
    pakistani-80s: pakistani-80s-painted
    pakistani-cinematic: pakistani-cinematic-painted
```

Rules, checked by `validate` and at startup:

- `fallbacks` requires named `prompts`.
- Both names must be configured prompts.
- A prompt cannot be its own fallback.
- Each fallback belongs to exactly one primary, and each primary has at most one fallback.
- Chains are rejected: a fallback cannot itself be a primary.

An absent or empty mapping keeps the previous behavior exactly.

Fallback variants are not queued when an image is detected; every other variant
is. A fallback is admitted through the normal admission path (staging, identity,
validation, pacing, bounded retries and audits all apply) only once the primary
job for the same source version (path, size, modification time and SHA-256) is
`FAILED` with `PROVIDER_PERMANENT` (a definitive provider rejection such as
moderation) or `PROVIDER_RETRYABLE` (a retryable provider failure whose bounded
retries were exhausted). An Azure HTTP 401/403 that carries a recognized
content-policy code (for example `moderation_blocked`) is such a
`PROVIDER_PERMANENT` moderation rejection: it does not stop the invocation and
it does trigger the configured fallback. If the same 401/403 also carries an
access code or a recognized access diagnostic (for example `Unauthorized`,
`PermissionDenied` or a firewall denial), it is treated as
`AUTHENTICATION_FAILED` and stops the invocation instead.

In both `process` and `watch`, the source version is the path, size,
modification time **and** SHA-256 content hash, and an existing fallback job
only counts when it belongs to the same content hash as the latest primary.
The two modes guard against the source changing before dispatch differently:

- In `process`, the current file is re-verified (size, modification time and
  SHA-256; links and reparse points never match) against the failed primary's
  version immediately before the fallback is dispatched; if it was rewritten
  meanwhile, the fallback is skipped (`fallback_skipped_source_changed`) and the
  new version is handled as a new image.
- In `watch`, a fallback that is waiting for its primary, or whose admission
  failed transiently (for example a locked file or staging backpressure), stays
  pending and is retried every cycle. It is dropped
  (`fallback_pending_dropped`) when the source's size or modification time no
  longer matches; the rewritten file is then evaluated on its own. `watch` does
  not re-hash the file for this check, so content replaced with an identical
  size and modification time during the pending window is not detected (the
  folder scanner cannot detect that case either). Repeated admission failures
  of the same pending version are logged (`watch_item_failed`) on the first
  failure and then only on attempts 2, 4, 8, ...; `fallback_pending_admitted`
  is logged once when it is finally admitted.

These outcomes never trigger a fallback:

- success: logs `Fallback not needed; primary succeeded.` once per run (a
  restarted `watch` reports it again, as it does saved failures);
- ambiguous outcomes;
- `AUTHENTICATION_FAILED` (an Azure 401/403 without a recognized content-policy
  code), which still stops the invocation and leaves queued work;
- `STATE_FAILED`, `INVALID_IMAGE`, `IMAGE_LIMIT_EXCEEDED`, `SOURCE_CHANGED`,
  `OUTPUT_*`, `PUBLICATION_FAILED` and `SHUTDOWN_INTERRUPTED`.

`watch` queues the fallback during the same run as soon as the primary fails,
logging for example
`[pakistani-cinematic-painted] Queued DSC_5476.JPG as fallback; primary pakistani-cinematic failed (PROVIDER_PERMANENT); 1 pending.`.
At startup the incoming folder is rescanned. If a primary for the current
source version already failed this way and no fallback job exists, the
fallback is queued once. An existing fallback job of any status is never
duplicated; it is handled like any other variant, so a successful one is
skipped. `process <image>` runs all non-fallback variants first, then the
fallbacks whose primaries qualified, in the same invocation. The exit code
still reflects the failed primary, so it is nonzero even when the fallback
succeeds. If the source changed or was removed, the normal admission rules
apply.

The relationship comes from configuration only: no schema migration or reset
is needed. Adding, changing or removing `fallbacks` never reopens terminal
jobs or reruns succeeded variants. Once admitted, a fallback is an ordinary
variant for `--retry-failed-variants`, `--retry-input-rejection` and
`--reset-state`.

**Named prompts require fresh state. Existing single-prompt queue databases
are not upgraded.** Single-prompt configuration continues using the original
schema and filenames. Switching modes in either direction fails before
sign-in or queue mutation. Choose a new destination, or explicitly archive the
existing internal history:

```powershell
python -m tcfcomic watch --config .\config.azure.interactive.yaml --reset-state
```

This command is **not a dry run**: after archival it starts watching, reprocesses
incoming originals, and can make additional billable requests. The actual
interactive configuration now uses `tcf-school` and `pakistani-80s`, so a
destination containing its old queue requires this explicit decision or a new
destination. No reset happens during ordinary startup or `validate`.

Reset moves the entire `.tcfcomic` directory to a unique sibling
`.tcfcomic-backup-<timestamp>-<suffix>` under the destination. It does not delete
or overwrite backups, source images, published PNGs, or custom quarantine
directories outside `.tcfcomic`. It refuses active processes, including older
versions holding the internal `runtime.lock`. A stable destination-level
`.tcfcomic-coordination.lock` remains outside the archived directory.
Before archival it waits interruptibly for the old queue's remaining request
and 429 cooldown, reporting a countdown. Ctrl+C while waiting leaves history
unarchived. Fresh internal state is created only by normal startup afterward.
Unknown, unsafe, or corrupt old state fails closed; read-only inspection uses
a bounded private SQLite/WAL copy (512 MiB total, at most one million attempts).
The accurate OS-clock assumption across restarts still applies.

The OpenAI provider is not local processing. For ordinary single-frame PNG,
JPEG, and WebP inputs it sends the immutable staged image bytes and the configured
transformation prompt to OpenAI's image-edit API. Eligible MPO inputs instead send
the normalized primary-frame PNG described above with that prompt.
Do not select OpenAI mode unless the operator is
authorized to send that content to OpenAI.

### Azure OpenAI

The public OpenAI example remains unchanged. For Azure, use the separate
`config.azure.example.yaml`; do not overwrite an existing `config.yaml`.
After installing the project, create a separate configuration:

```powershell
if (Test-Path .\config.azure.yaml) { throw "config.azure.yaml already exists; edit it instead." }
Copy-Item .\config.azure.example.yaml .\config.azure.yaml
New-Item -ItemType Directory -Force C:\Images\Incoming | Out-Null
New-Item -ItemType Directory -Force C:\Images\Transformed | Out-Null
notepad .\config.azure.yaml
```

Edit the local absolute paths and replace `provider.model` with your actual
Azure image-edit **deployment name**, not merely a model family name.
`tcfcomic-gpt-image-2` is a clearly labeled placeholder: no deployment,
underlying model, availability, or access has been verified or provisioned.
The example targets `https://sweepertestai.openai.azure.com`.
The default transformation prompt is the same as the public OpenAI example;
you may replace it with your own instruction.

Validate offline first, without a key or provider call:

```powershell
python -m tcfcomic validate --config .\config.azure.yaml
```

Validation checks local configuration and paths, not remote deployment
existence or permissions. Its summary includes the canonical endpoint and
deployment name, but excludes the prompt and credentials. `validate` performs
read-only directory checks: it does not create write probes, initialize state,
inspect images, or sign in. It does not establish destination writability.

When authorized to make paid requests, load your Azure key into the current
PowerShell session without typing its literal value on the command line (a
literal assignment would be saved in PSReadLine history), then process or watch:

```powershell
$secureKey = Read-Host "Azure OpenAI API key" -AsSecureString
$credential = [System.Management.Automation.PSCredential]::new("azure-openai", $secureKey)
$env:AZURE_OPENAI_API_KEY = $credential.GetNetworkCredential().Password
Remove-Variable secureKey, credential
python -m tcfcomic process --config .\config.azure.yaml "C:\Images\Incoming\photo.jpg"
python -m tcfcomic watch --config .\config.azure.yaml
Remove-Item Env:\AZURE_OPENAI_API_KEY
```

In the default `provider.authentication: api_key` mode, only
`AZURE_OPENAI_API_KEY` authenticates Azure requests. An unrelated
`OPENAI_API_KEY` may coexist but is never used as a fallback. Other
`OPENAI_*` and `AZURE_OPENAI_*` environment settings are rejected in Azure
mode, including endpoint, custom-header, and token overrides. Credentials
belong in neither YAML nor command arguments. Both keys, including their
whitespace-trimmed forms, are redacted from diagnostic logs, quarantine
metadata, and attempt-state diagnostics.

#### Troubleshooting API-key configuration

The key must be present in the **same PowerShell process that starts TCFComic**.
A key entered in another terminal is not inherited by an already-running terminal
or assistant session. Checking YAML with `validate` does not check the key or
authenticate against Azure.

To enter a key without putting its literal value in command history:

```powershell
$secureKey = Read-Host "Azure OpenAI API key" -AsSecureString
$credential = [System.Management.Automation.PSCredential]::new("azure-openai", $secureKey)
$env:AZURE_OPENAI_API_KEY = $credential.GetNetworkCredential().Password
Remove-Variable secureKey, credential

# Check presence without printing the key.
-not [string]::IsNullOrWhiteSpace($env:AZURE_OPENAI_API_KEY)

# Starts processing; use only when ready to authorize provider requests.
python -m tcfcomic watch --config .\config.azure.yaml

# After stopping the watcher, remove the key from this shell.
Remove-Item Env:\AZURE_OPENAI_API_KEY
```

The environment value is necessarily plaintext in process memory; do not print
it, save it in YAML, or paste it into diagnostic reports.

- `CREDENTIAL_MISSING`: the environment variable is absent or blank; no image
  request was sent.
- Queue-mode mismatch: an older single-prompt YAML cannot open a named-prompt
  queue. Synchronize `provider.prompts` with the intended named configuration
  rather than resetting a working queue merely to change authentication.
- HTTP 401/403: setting a key locally is not proof Azure accepts it. The key and
  root endpoint must belong to the intended resource, and resource/network
  restrictions still apply. If the resource has `properties.disableLocalAuth`
  set to `true`, key-based authentication is disabled. Use the interactive
  configuration or consult your administrator; do not bypass organizational
  policy just because a portal displays a key.

### Read-only Azure access diagnostic

Run this in the **same PowerShell window** that holds `AZURE_OPENAI_API_KEY`:

```powershell
python -m tcfcomic check-auth --config .\config.azure.yaml
```

For an approved interactive identity instead (one browser sign-in, memory-only
credential cache, then silent token acquisition):

```powershell
python -m tcfcomic check-auth --config .\config.azure.interactive.yaml
```

Unlike offline `validate`, `check-auth` makes **one read-only network request**:
`GET https://<resource>.openai.azure.com/openai/models?api-version=2024-10-21`,
the Microsoft Learn **Models - List** operation for API version `2024-10-21`.
It sends only the configured authentication mode: `api-key` from
`AZURE_OPENAI_API_KEY`, or an interactive bearer token, never both or a fallback.
No image, prompt, model/deployment payload, generation call, automatic pagination,
redirect, or retry is sent. Ambient OpenAI/Azure overrides are rejected using the
same policy as the image provider; HTTP proxy/environment inheritance is disabled
and normal TLS verification remains enabled.

The check does not open, create, or modify the queue, logs, destination folders,
or runtime lock. It may run alongside a watcher and does not create a generation
attempt or consume its local RPM pacing slot; Azure can still apply service
limits to this GET. The total check budget, including interactive sign-in, is
the lesser of 30 seconds and `provider.request_timeout_seconds`, with bounded
worker/credential-process cleanup afterward. A killable, memory-only HTTP worker
also bounds stalled platform DNS resolution. Ctrl+C returns 130 and closes the session.
Success requires HTTP 200 with a bounded JSON catalog `data` list (at most 1 MiB,
without compressed bodies); neither model names nor catalog contents are printed.

**Success proves only that this catalog request was accepted.** The catalog
route differs from the image-edit route; it does not prove image-edit permission,
deployment availability, or successful generation. HTTP 401/403 returns exit 3
with static diagnostics and a validated request ID when available. HTTP 404
means the probe is unavailable, not necessarily a bad key; 429 means wait before
an operator-initiated check; 5xx, transport failures, unexpected statuses, and
invalid/oversized responses are inconclusive (exit 5). There are no retries.

Error bodies are bounded to 64 KiB and never printed. Only allowlisted literal
codes or exact known resource phrases can produce resource-reported
authentication-policy/network hints. Unknown, numeric, or missing codes remain
unknown. A generic 403 lists possible permission, network, or authentication
policy restrictions; **disabled keys are only a possibility in API-key mode**,
not a finding about your resource. Portal key visibility is not policy proof.
Share only this command's safe summary, never your key or raw error body.
This diagnostic does not change resource policy or resolve the reported 403.
Contact the administrator or use an approved interactive identity as appropriate.

For image processing, a definitive Azure HTTP 401 or 403 now durably records the
first attempt as FAILED, including its completion time, safe diagnostic and
applicable cooldown, **before** fallible quarantine cleanup. Both `process` and
`watch` then stop with exit 3, even if cleanup fails. Other already-queued variants
remain unattempted with their staging intact; variants not yet admitted remain
in the source. No global auth-block state is stored: each later invocation can
encounter a first denial again if access is still restricted.

Exception: a 401/403 carrying a recognized content-policy code such as
`moderation_blocked` is a moderation rejection (`PROVIDER_PERMANENT`), not an
access failure. It does not stop the invocation, the remaining variants still
run, and a configured fallback is admitted.

Previously failed jobs, including variants that already failed with 403, remain
closed with their original attempts; this fix does not retry, reset, or migrate
them. Switching API-key/interactive authentication still changes request identity
and fails closed for pending work. Resuming under another identity requires an
explicit operator-selected, approved queue/configuration path, not automatic
adoption or a force-403 recovery option.

The two local configurations are separate files, not automatically synchronized.
Keep prompts, limits, retry policy and folder settings aligned when desired;
retain `authentication: api_key` without interactive-only tenant/client/redirect
settings in the API-key file. Changing authentication changes queued request
identity: it does not silently convert pending interactive jobs into API-key jobs,
rerender successful outputs, or unlock moderation-blocked failures.

Azure image processing is **not local processing**: each image-edit request sends your staged image and
configured prompt to the configured Azure resource. Only send content you
are authorized to disclose there. JPEG and PNG upload bytes are unchanged,
with explicit MIME types even though staging uses `.input` filenames.
Eligible MPO inputs upload as `input.png` with MIME type `image/png`.
WebP is decoded and converted in memory to RGB/RGBA PNG; decoded pixels and
alpha are preserved, but WebP metadata is not promised to survive. Conversion
enforces `limits.max_input_bytes` during PNG encoding. An expanded PNG that
exceeds this limit fails permanently before HTTP dispatch (exit 4); source
and staged bytes are never rewritten. Existing input validation and limits
still apply.

Azure endpoints must be HTTPS resource roots with a single ASCII resource
label under `.openai.azure.com`. Casing and a trailing slash are canonicalized;
ports, credentials, paths, queries, fragments, and alternate domains are
rejected. The adapter uses the pinned ordinary OpenAI SDK client, not its
deployment-path-rewriting Azure client, and sends
`POST /openai/v1/images/edits?api-version=preview` with the configured
authentication mode, deployment name, and PNG output. API-key mode sends
only `api-key`; interactive mode sends only `Authorization: Bearer ...`.
SDK retries, redirects, and proxy/environment inheritance are disabled.
The explicit request timeout and application retry/ambiguity rules below
are identical to public OpenAI.

Pending Azure jobs include the canonical endpoint and the `preview` contract
in their existing request-identity hash. Changing the endpoint rejects queued
READY/READY_RETRY work before another provider call; equivalent casing or a
trailing slash does not cause drift. Public OpenAI/fake identity remains
unchanged. Pacing and retry settings are not part of request identity.

### Durable request pacing

Both Azure examples and the local Azure configurations use
`provider.requests_per_minute: 2`. The active interactive configuration uses
`retry.max_attempts: 4` (one initial submission plus **three retries**); the
examples and other configurations retain conservative defaults.
This permits **at most 2 requests/minute**, conservatively waiting at least
**30 seconds after completion** of each provider attempt before starting the
next one. Five instantaneous requests start at 0, 30, 60, 90, and 120 seconds;
real requests take time, so there may be fewer requests and fewer successes.
Every recorded attempt counts, including failed, ambiguous, and retry attempts.
Rejected input or failed token refresh before an attempt does not consume a slot.

`requests_per_minute` is optional (omit it for legacy unpaced operation), and
must be an integer from 1 through 60000, not null. It applies to the entire
destination's durable SQLite queue, not separately to each image. There are
no accumulated burst credits. HTTP 429 cooldowns are queue-wide even when
pacing is omitted: the larger of the bounded exponential retry delay and the
server delay applies, including after the final exhausted attempt. Azure
`retry-after-ms` takes precedence over `Retry-After` seconds or an HTTP date.
Invalid, negative, nonfinite, or over-86400-second headers are ignored in favor
of a valid fallback or local backoff. Without the expanded policy, HTTP 409
remains a per-job retry. With that policy enabled, a valid retry hint on any
eligible HTTP response also establishes a queue-wide cooldown; an absent
non-429 hint does not. SDK retries remain disabled.

A final eligible 429 is durably recorded as a retryable provider outcome together
with its FAILED job status and queue-wide cooldown, before quarantine I/O.
Quarantine failure cannot leave exhausted work READY_RETRY; increasing the
configured budget alone cannot reopen it. Eligible recovery still requires
matching failure provenance and the explicit recovery flag.
Terminal 429 refusals instead persist FAILED and their applicable cooldown
before quarantine I/O; they never enter READY_RETRY.

The queue and server waits survive restart. The earlier pacing implementation
uses a nullable attempt metadata column; this response-retry feature adds no
tables, columns, or database migration. Existing jobs and attempts are
preserved. Local pacing uses existing completion timestamps. Interrupted
attempts are recovered as ambiguous without replay, and conservatively spaced
from recovery; legacy unfinished rows are not treated as free slots.
In a running process, scheduler UTC timestamps advance from its startup UTC
epoch using monotonic elapsed time, so forward or backward wall-clock changes
cannot shorten waits. Restart reconstructs remaining waits from persisted UTC:
this assumes an accurate OS clock across restarts (a forward correction while
stopped can shorten a reconstructed wait). Keep the system clock synchronized.

While throttled, watch continues scanning/admitting each poll interval and,
with pacing configured, rescans after at most one dispatch. Existing staging
capacity/backpressure still applies; files over capacity stay unacknowledged
in the source for later admission. A single active provider call may delay
discovery until it finishes. One-shot processing waits only for its target
and never claims unrelated work. Ctrl+C interrupts waits promptly, leaving
queued status, retry due time, and attempt count unchanged; resume by restarting.
Token refresh occurs only after the rate/due gate, never before a rate wait.
Startup interactive sign-in remains unchanged; no access token is serialized
as scheduler state.

Restart an existing watcher to load changed configuration. Other clients
using the same Azure deployment, token quotas, or service limits can still
cause 429 responses: pacing coordinates this destination only, not other apps.
Retries are bounded by `retry.max_attempts`; invalid auth/content or ongoing
service failures are not guaranteed to succeed. Successful outputs retain
the existing exactly-once publication/recovery behavior.

### Opt-in Azure response retries and failed-variant recovery

The active `config.azure.interactive.yaml` enables:

```yaml
retry:
  max_attempts: 4
  azure_response_retries: true
  initial_delay_seconds: 10
  max_delay_seconds: 120
```

`azure_response_retries` is an optional boolean, default `false`. Enabling it
requires Azure and limits `max_attempts` to at most four **total lifetime**
attempts per variant, including attempts before restart or explicit recovery.
The setting is not part of request identity because it does not change the
provider payload. The shared 2-RPM completion gate still applies, so a retry
waits for the later of exponential backoff, a valid provider retry hint, and
the destination-wide gate (at least 30 seconds after completion at 2 RPM).
An exhausted response remains terminal, and its recorded global cooldown
still delays other work.

This opt-in allows retries only for definitive Azure HTTP responses:
unclassified 400, 408, 409, 429, and 500-599. Recognized authentication,
permission, content-policy, invalid-parameter, model/route/API unsupported,
and quota/billing-exhaustion codes are not retryable. Known parameter
indications also block retry. Unknown or uninspectable structured code paths
fail closed rather than being treated as an unclassified 400. Definitive Azure
HTTP 401/403 stop the invocation with exit 3 in both API-key and interactive modes,
unless they carry a recognized content-policy code (then they are moderation
rejections, as for 400).
Other statuses, including 404/405/413/422, are ineligible. On **all policies**,
including default Azure and public OpenAI, moderation, other recognized terminal
classifications, known parameters and unknown/uninspectable classifications
veto retries, even on HTTP 409/429/5xx. No prompt is rewritten to evade a filter.

**Repeated dispatch, especially after HTTP 408/5xx, can incur additional
charges even if an earlier request produced no usable response. There is no
guarantee of two pictures:** permanent refusals and exhausted budgets can leave
one or more variants without output. Transport timeout, connection loss,
cancellation, and recovered unanswered in-flight requests remain ambiguous
and are not replayed. SDK retries remain disabled.

Changing prompts or increasing an attempt limit does **not** automatically
reopen terminal provider failures. To explicitly reconsider the failed
variant(s) for one source using **current configured prompts**, run personally
when ready to authorize another potentially paid dispatch:

```powershell
python -m tcfcomic process --config .\config.azure.interactive.yaml --retry-failed-variants .\incoming\DSC_5326.JPG
```

This command targets only that source and its configured slots, skipping
successful variants without changing their identities or attempt histories.
For eligible failed variants across observed incoming sources instead:

```powershell
python -m tcfcomic watch --config .\config.azure.interactive.yaml --retry-failed-variants
```

Both commands require the enabled Azure policy and reject invalid policy
configuration before authentication. Watch reconsiders each observed source
version/slot once per invocation, acknowledging the source after its slots
have been considered. Restarting with the flag cannot reset the lifetime
budget. Legacy single-prompt state and named variants retain their respective
schemas. `--reset-state` is not needed and cannot be combined with this
recovery flag. The distinct `--retry-input-rejection` option may coexist;
its strict zero-attempt, JPEG-named MPO input-rejection guards are unchanged.

Recovery requires exact source path/name, size, mtime and hash, freshly staged
decoding under current limits (JPEG/PNG/WebP or supported MPO), and no old
stage, output/scratch reference, or candidate publication artifact. The
failure JSON must match the job, variant, provider stage, error, and last
completed attempt's exact safe diagnostic. Only the application's bounded
canonical HTTP diagnostic grammar with `Classification evidence v1 complete.`
is accepted, not loose status text. Only the provider adapter writes this suffix,
after inspecting a real SDK response and its bounded original envelope.
Duplicate JSON members, unsupported shapes and unreadable evidence fail closed.
Every historical attempt must independently qualify: a later eligible response
cannot erase earlier moderation, uncertainty, or unversioned evidence.
Historical ambiguous jobs require a recorded definitive 408/5xx; unanswered,
unfinished, malformed, unknown, permanent, or insufficient evidence is skipped
with a reason. Contiguous attempt numbering and remaining budget are required.

Before committing recovery, immutable per-failure/per-transition audits retain
the original failure bytes, provenance and attempt-history digests, old/new
complete request fingerprints, source/variant binding, and remaining budget.
No raw prompt, credential, or endpoint is recorded. A transaction rechecks the
whole job and attempt snapshot plus source, artifacts, and audit evidence
before adopting the fresh stage and current request identity. Job ID,
creation time, source/variant binding, and all historical attempt rows remain
unchanged. Audit files are **prepared authorization evidence, not proof of
commit**: a precommit crash requires the same explicit flag again; committed
queued work survives restart without resetting attempts only if its entire
history and the current retry policy still authorize continuation. Later failures or
different identity transitions produce distinct audit filenames.

The same whole-history guard runs before every repeat dispatch, including
ordinary READY/READY_RETRY restart without any recovery flag, before per-attempt
token acquisition. A final transaction compares the authorized job and full
attempt snapshot again before inserting an attempt. Denied queued work becomes
FAILED/STATE_FAILED (targeted process exit 6); watch reports it and continues.
Original attempts, failure records, audits, artifacts and cooldowns are retained.
Zero-attempt work and artifact-only publication recovery are not response replay.
Genuine SDK connect/connect-timeout/pool-timeout failures have separate
`Dispatch evidence v1 not-sent.` proof for automatic continuation; that proof
alone cannot authorize explicit failed-response recovery.

### Interactive Microsoft Entra sign-in (Azure only)

Update the installation using the pinned dependencies, including
`azure-identity==1.25.3`, and create a separate config (leave existing configs alone):

```powershell
python -m pip install -c constraints.txt -e ".[dev]"
if (Test-Path .\config.azure.interactive.yaml) { throw "Config already exists; edit it instead." }
Copy-Item .\config.azure.interactive.example.yaml .\config.azure.interactive.yaml
New-Item -ItemType Directory -Force C:\Images\Incoming | Out-Null
New-Item -ItemType Directory -Force C:\Images\Transformed | Out-Null
notepad .\config.azure.interactive.yaml
python -m tcfcomic validate --config .\config.azure.interactive.yaml
python -m tcfcomic process --config .\config.azure.interactive.yaml "C:\Images\Incoming\photo.jpg"
python -m tcfcomic watch --config .\config.azure.interactive.yaml
```

Edit the absolute paths and replace `REPLACE_WITH_YOUR_DEPLOYMENT_NAME` with
your real image-edit deployment name. The example endpoint is
`https://sweepertestai.openai.azure.com`; no deployment or live Azure access
has been verified. With `provider.authentication: interactive`, process and
watch open your browser for Microsoft sign-in before constructing the processor
or admitting work, including an empty watch folder. No API key, `az login`,
manually supplied bearer token, or environment credential chain is needed or
used as a fallback. `validate` and `--help` never construct credentials or
start browser, sign-in, or network activity.

The default shared development public app is suitable only for this local MVP;
corporate tenant policies may block it. An administrator may require an
approved **public client** app registration. In that case configure its
`client_id` GUID and matching registered `redirect_uri` together, and normally
your `tenant_id` GUID. Enable the app's public-client interactive desktop
sign-in flow and register the exact loopback redirect, such as
`http://localhost:8400/`. Only HTTP `localhost`, an explicit port 1024-65535,
and the root path are allowed; URL credentials, queries, and fragments are
rejected. `tenant_id` may also be set alone with the development app.
Do not add a client secret. Your signed-in identity needs the
**Cognitive Services OpenAI User** role on the Azure resource and access to
the deployment. Tenant consent, Conditional Access, and MFA policies still
apply; this application does not bypass them.

Sign-in uses the fixed Microsoft public-cloud authority and
`https://cognitiveservices.azure.com/.default` scope. Its transport does not
inherit environment proxy or CA-bundle overrides. One dedicated subprocess
owns the credential and its **in-memory-only** cache. No token/account cache
is persisted; sign in again on each invocation. Startup is bounded to 300
seconds and silent token acquisition to 60 seconds. Later acquisitions do not
open a browser automatically. If refresh requires interaction or a token
cannot cover the request timeout plus a 35-second allowance, processing stops
with `AUTHENTICATION_FAILED` (exit 3): restart and sign in again.
Auth cancellation returns 130 for process and 0 for watch.

Tokens are obtained before queue claims, so failed/cancelled refresh does not
alter queued status, attempts, or retry due times. Only short-lived access
tokens travel to image subprocesses in private spawn memory, never in
command-line arguments, environment variables, configuration, SQLite, logs,
or failure artifacts. No refresh credential leaves the auth subprocess.
Expired pre-dispatch tokens fail permanently; a real HTTP 401 is not refreshed
and resubmitted, avoiding duplicate paid requests. A definitive Azure HTTP 401/403
stops the invocation with exit 3 and a permanent failed attempt in either auth
mode. Public OpenAI behavior is unchanged. Uncertain transport failures
retain the existing ambiguous/no-replay behavior. Watch stops on terminal auth
failure rather than attempting every remaining file.

Interactive request identity adds the authentication mode and effective
tenant/app/redirect settings to a new hash version. Changing these settings or
switching auth modes rejects pending READY/READY_RETRY work before image
requests; unchanged terminal source files are not replayed. Legacy API-key,
public OpenAI, and fake hashes remain unchanged. This is a local interactive
workflow; unattended/service authentication is out of scope. Verification
uses offline credential/browser and transport mocks, not live Azure sign-in.

## Commands

### All command-line options

| Option or argument | Commands | Purpose |
|---|---|---|
| `--config <file>` | `validate`, `check-auth`, `process`, `watch` | Required YAML configuration path. |
| `<image>` | `process` | Required path to one image directly inside the configured source folder; processes all configured prompt variants (fallback variants in `provider.fallbacks` only after their primary was rejected by the provider). |
| `--reset-state` | `watch` | Archive internal history and reprocess incoming images. Preserves original images and existing outputs; additional provider charges may apply. |
| `--retry-failed-variants` | `process`, `watch` | Explicitly reconsider eligible failed Azure responses using current prompts. Requires `retry.azure_response_retries: true` with Azure; retains lifetime attempts and skips successful variants. |
| `--retry-input-rejection` | `process`, `watch` | Reconsider eligible JPEG-named MPO input rejections with zero historical provider attempts, using current settings. Not a general force-reprocessing flag. |
| `-h`, `--help` | Root command and all subcommands | Show available commands or command-specific options without signing in or processing images. |

### Basic examples

Validate configuration without creating a provider or requiring credentials:

```powershell
python -m tcfcomic validate --config .\config.yaml
```

Check Azure catalog access once (networked, no images or queue access):

```powershell
python -m tcfcomic check-auth --config .\config.azure.yaml
python -m tcfcomic check-auth --config .\config.azure.interactive.yaml
python -m tcfcomic check-auth --help
```

Process one direct child of the configured source directory:

```powershell
python -m tcfcomic process --config .\config.yaml "C:\Images\Incoming\photo.jpg"
```

Watch the configured source directory:

```powershell
python -m tcfcomic watch --config .\config.yaml
```

Watch mode is intentionally non-recursive. Existing eligible files are treated
as backlog. A file becomes eligible only after its size and modification time
remain unchanged for the configured stability period.

### Azure interactive examples: every run option

Run these from the `TCFComic` directory. The image argument must name an existing
direct child of the source folder in your configuration. Stop a running watcher
with **Ctrl+C** and let it exit before starting another command against the same
destination (`check-auth` does not require that lock). Processing and watching may open corporate sign-in and incur
provider charges; validation and help do not.

```powershell
# Validate configuration only.
python -m tcfcomic validate --config .\config.azure.interactive.yaml

# One read-only catalog GET after sign-in, not an image permission check.
python -m tcfcomic check-auth --config .\config.azure.interactive.yaml

# Normal watching: process new source versions and newly configured prompt names.
# Configured fallbacks (painted prompts) are queued only after their primary is rejected.
python -m tcfcomic watch --config .\config.azure.interactive.yaml

# Process one image with all configured prompts; successful variants are skipped;
# fallbacks run after the primaries, only for primaries the provider rejected.
python -m tcfcomic process --config .\config.azure.interactive.yaml .\incoming\photo.JPG

# Reconsider eligible provider failures for one image, retaining attempt history.
python -m tcfcomic process --config .\config.azure.interactive.yaml --retry-failed-variants .\incoming\photo.JPG

# Watch and reconsider eligible provider failures across incoming images.
python -m tcfcomic watch --config .\config.azure.interactive.yaml --retry-failed-variants

# Reconsider one eligible never-sent JPEG-named MPO input rejection.
python -m tcfcomic process --config .\config.azure.interactive.yaml --retry-input-rejection .\incoming\photo.JPG

# Watch and reconsider eligible never-sent JPEG-named MPO input rejections.
python -m tcfcomic watch --config .\config.azure.interactive.yaml --retry-input-rejection

# Combine both guarded recovery options for one image.
python -m tcfcomic process --config .\config.azure.interactive.yaml --retry-input-rejection --retry-failed-variants .\incoming\photo.JPG

# Combine both guarded recovery options while watching.
python -m tcfcomic watch --config .\config.azure.interactive.yaml --retry-input-rejection --retry-failed-variants

# Start over: archive history, then watch and reprocess ALL incoming images.
# This can regenerate previously successful variants and incur additional charges.
python -m tcfcomic watch --config .\config.azure.interactive.yaml --reset-state

# Root and command-specific help; -h is an alias for --help.
python -m tcfcomic --help
python -m tcfcomic validate --help
python -m tcfcomic process --help
python -m tcfcomic watch --help
python -m tcfcomic check-auth --help
python -m tcfcomic watch -h
```

**Choose the appropriate command; do not run the entire example block in sequence.**
`--reset-state` cannot be combined with `--retry-failed-variants` and is not
needed merely to add another named prompt. There is no `--force` option or
command-line prompt selector. Neither recovery flag bypasses moderation,
unverifiable history, source-integrity checks, or its applicable attempt limits.
Missing published outputs should be restored at their recorded names, not
submitted again as provider failures.

Prompt text, source/destination folders, rate limits, heartbeat intervals, and
retry policy are configured in YAML, not additional command-line parameters.

### Console progress and heartbeats

`logging.console_format` accepts only `json` (the backward-compatible default)
or `text`. Azure examples and local Azure configurations select `text`.
`watch.heartbeat_seconds` defaults to 15 and accepts finite numbers from 1 through
3600. For example:

```yaml
watch:
  heartbeat_seconds: 15
logging:
  console_format: text
  level: INFO
```

At INFO, timestamped console lines report watching, first detection/waiting for
stability, durable queue admission and pending count, processing attempt `1/4`
with the active interactive configuration,
elapsed processing time, rate/retry waits, validated/published completion, safe
failures, duplicate skips, and watcher shutdown. Elapsed time is not a percentage
or a promise of completion. A rejected input is reported even when it fails
before any provider attempt. Detection and skips are reported once per observed
size/mtime version, not every poll.

The parent process emits heartbeats while its provider subprocess is active or
its authentication broker is waiting for token refresh. Authentication heartbeats
report elapsed waiting time and pending count, not a provider attempt.
Idle and queue waits wake for the heartbeat or shutdown without consuming quota,
claiming jobs, acquiring tokens, or changing finish-plus-30-second pacing.
Token-refresh entry is also logged before waiting; interactive startup sign-in retains
its browser notice. Source discovery can still wait until the active call finishes.
Higher configured log levels suppress INFO progress. Console output goes to
stderr; one-shot stdout remains the output path. Rotating file logs always stay
JSONL, with the same allowlisting/redaction rules and no prompts, tokens, EXIF,
raw headers, or fabricated progress percentages.

## Reliability and recovery

- Source files are opened read-only, copied to immutable staging, and never
  renamed, overwritten, moved, or deleted.
- Pillow verifies and fully decodes each input before provider dispatch. Byte,
  dimension, pixel, decompression, format, and single-frame limits are enforced,
  with only the primary-MPO input exception described above.
  Input byte, dimension, pixel, and decompression-limit failures are input
  failures and return exit code 4.
- Staged input integrity and configured limits are checked after staging during
  admission and, after an atomic queue claim, immediately before each provider
  attempt. The verifier requires the exact durable path to be a direct staging
  child and an existing regular non-reparse file, streams and compares its size
  and SHA-256 with durable identity, validates the supported input, and requires
  non-following file metadata (excluding access time) to remain unchanged. The
  provider subsequently reopens the staged path, leaving an accepted local
  path-reopen TOCTOU residual between verification and the provider read; it is
  not eliminated.
- SQLite state under `DESTINATION\.tcfcomic\state.db` records source-version
  identity, attempts, due retry times, publication intent/digests, and terminal
  status. Discovery only admits durable work; provider dispatch is driven by
  atomic queue claims. A per-job delayed retry does not block another ready
  image, but local pacing and provider-wide 429 cooldowns gate the entire queue. An unchanged
  successfully processed source is not submitted again after restart; modified
  source bytes create a new eligible version.
- By default, only proven pre-dispatch connect, connect-timeout, and pool-timeout failures,
  plus completely classified eligible HTTP 409 and 429 responses, use bounded
  application-controlled retries. Terminal/unknown classifications always veto.
  Permanent and exhausted failures create sanitized JSON metadata under the
  configured quarantine directory while leaving the source untouched.
- By default, eligible HTTP 408/5xx responses, provider timeouts, and interrupted
  in-flight requests are marked ambiguous and are not automatically replayed,
  avoiding an accidental duplicate paid call. The explicit Azure response
  policy above changes only qualifying HTTP responses, never unanswered
  transport failures. Eligible historical failures require the recovery flag.
- Provider output is written to an exclusive temporary file, fully decoded as
  PNG, hashed, and assigned a durable final leaf before it is renamed on the
  destination volume. Restart recovery reconciles either the temporary or final
  path against the recorded digest. Existing output files are never overwritten.
  Provider/output byte, dimension, pixel, and decompression-limit failures
  return exit code 5; malformed provider responses and invalid output images
  also remain provider/output-invalid failures at exit code 5.
- One runtime lock and one active provider attempt limit cost and state races.
- Watch mode continues after corrupt images and item-level provider failures.
- An old local `FAILED` / `INVALID_IMAGE` MPO rejection can be safely reconsidered
  when that exact source is observed again (including after restart). This is
  **only** for a now-valid JPEG-named MPO with zero historical attempt rows,
  unchanged request identity and exact source path key/size/mtime/SHA-256, and no
  publication/output/scratch artifacts. A bounded regular non-reparse quarantine
  record must prove matching input-stage failure with zero attempts. Its exact
  bytes are preserved as an exclusive, fsynced
  `<job-id>.mpo-input-failure.json` audit copy before a transactional compare-and-swap
  requeues the same job. An identical audit left after interruption is verified,
  never overwritten; differing evidence blocks recovery. The job ID, creation
  time, identity, and attempt history are retained. This is not a startup-wide
  reset, does not recover arbitrary format failures, and never reopens sent,
  ambiguous, provider-failed, or successful work. Rate configuration changes alone
  do not change request identity.
- Changed request settings (including a changed prompt) block this recovery by
  default in both `process` and `watch`. To explicitly adopt the **current**
  provider, model, prompt, Azure endpoint, and authentication binding for one
  eligible never-sent MPO rejection, run:

  ```powershell
  python -m tcfcomic process --config .\config.azure.interactive.yaml --retry-input-rejection "C:\Images\Incoming\photo.jpg"
  ```

  This explicit process/watch flag relaxes request-identity equality, not the source,
  provenance, zero-attempt, fresh image-validation, or artifact guards above.
  It is not force reprocessing: new files use ordinary admission, successful jobs
  remain idempotent, and pending jobs retain their dispatch identity checks.
  Identity adoption and requeue happen atomically on the same job. The original
  byte-exact audit is retained, with deterministic digest-keyed
  `<job-id>.mpo-request-transition.<sha256>.json` preparation evidence containing
  only source binding, provenance digest, and complete old/new request-identity
  fingerprints, never raw prompt, endpoint, or credentials. Preparation evidence
  is not proof of commit or permission to retry: after a precommit interruption,
  the explicit flag is still required. No transition audit is added if identity
  did not change. Executing this command may perform a billable provider request.
  Ordinary interactive sign-in still happens first, even for an idempotent success.
  Verification of this recovery uses synthetic inputs and fake providers, not
  a live-service retry.

Output names use a Windows-safe source stem plus durable content and job
identifiers:

```text
<sanitized-source-stem>__<source-sha256-first-12>__<job-id-first-8>.png
```

Logs use the selected console format and always JSON Lines in
`DESTINATION\.tcfcomic\logs\tcfcomic.jsonl`. Logs and quarantine records use
allowlisted fields and exclude credentials, prompts, image bytes, base64 data,
headers, and complete provider payloads.

### Troubleshooting provider HTTP failures

At the default INFO logging level, a failed `provider_attempt` includes a safe
`message` on the console and in the JSONL log. Attempt state and quarantine
metadata retain the same diagnostic. When available, it includes the HTTP
status and a recognized, allowlisted provider code (for example,
`HTTP 404` and `DeploymentNotFound`), plus static checks to consider:

| Status | Checks, not a proven cause |
|---|---|
| 400 | Request parameters and model support for the image-edit API |
| 401 | Credential validity and the intended resource; Azure exits 3 in either auth mode |
| 403 | Resource permissions, authentication policy, and network access rules; Azure exits 3 (unless a recognized content-policy code marks it a moderation rejection); key-based auth being disabled is only one possibility in API-key mode |
| 404 | Resource, deployment name, and API route |

Recognized codes are read from the SDK error and fixed nested error-object paths,
including Azure's `contentFilter`, `ResponsibleAIPolicyViolation`, and
`moderation_blocked`. These are terminal policy rejections, not transient
failures; neither default nor expanded response retries resubmit them. A reported
content-policy code can refer to either the input or the generated output;
HTTP 400 alone does not prove a policy rejection. Known parameter names and
strictly validated hexadecimal/UUID request IDs are included when available
to help correlate a failure with provider support. No other header values or
free-form provider messages are copied into diagnostics.

Unknown or malformed provider codes and raw response text are not reported;
an uninspectable structured classification is indicated only by a static marker.
With the default policy, completely classified eligible HTTP 408/5xx remain
ambiguous without automatic replay; eligible 409/429 retain bounded retries.
Terminal or unknown classification overrides either status rule. The opt-in Azure exceptions are
described above. These diagnostics do not establish a service root cause
or recover HTTP details from older generic failure records.
Older writers could omit structured classifications (including `type`) from
otherwise canonical diagnostics. Such records cannot prove the absence of
terminal or unknown evidence. Unversioned histories are therefore rejected by
both explicit recovery and ordinary queued continuation, including earlier
attempts even if the last response has new complete evidence. No date, operator
flag or current software version upgrades old evidence. No existing attempts
or audits are rewritten or attested. This deliberately sacrifices availability
of old transient failures rather than guessing that replay is safe.
Do not roll back to an older binary without retaining these guards: disabling
the opt-in or recovery flag alone does not protect queued retries.

For safe reporting, share only the job ID, application error code, HTTP status,
recognized provider code, validated request ID, and safe diagnostic message. Never share keys, tokens,
prompts, request headers, or raw provider responses.

Apart from guarded never-sent MPO recovery and explicit eligible failed-response
recovery above, an unchanged failed source is terminal in the ledger: running
`process` again with that same source does not call the provider again.
Changing configuration alone does not authorize terminal-failure recovery.
Do not delete or reset the state database, or rename inputs to evade the
lifetime attempt budget.

## Shutdown

Press `Ctrl+C` to stop watch mode. Discovery stops immediately. An interrupted
provider child is terminated and, if necessary, killed and joined within the
configured monotonic shutdown deadline. The attempt is left in durable
ambiguous state, and no partial final PNG is published. Queued but unstarted
files remain eligible on restart. Normal termination signals use the same path.

The OpenAI adapter rejects ambient `OPENAI_*` configuration other than
`OPENAI_API_KEY`, pins the official HTTPS API base URL, disables SDK retries,
uses an explicit timeout, disables redirects for both public and Azure clients,
and disables proxy/environment inheritance in its
HTTP client. For public OpenAI (and Azure without the opt-in response policy),
eligible HTTP 408/5xx responses, timeouts, connection resets,
protocol failures, and unknown post-dispatch failures are treated as ambiguous
rather than replayed automatically.
Successful response correlation IDs use the same strict bounded hexadecimal/UUID
grammar before worker handoff and persistence. Invalid metadata is omitted;
a valid PNG still succeeds.

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | Success or graceful watch shutdown |
| 2 | CLI or configuration error |
| 3 | Missing the selected provider's `OPENAI_API_KEY` or `AZURE_OPENAI_API_KEY`, or interactive authentication failure, or definitive Azure HTTP 401/403 without a recognized content-policy code |
| 4 | Invalid, unsupported, changed, outside-source, or input-limit failure |
| 5 | Provider permanent, exhausted, ambiguous, output-limit, invalid-output failure, or inconclusive catalog probe |
| 6 | State, publication, lock, or internal failure |
| 130 | Interrupted one-shot operation |

Ordinary item failures do not set the eventual watch-mode exit code; definitive
Azure access denial stops the watcher with exit 3.

## Offline tests

Install the constrained development dependencies from `pyproject.toml` and
`constraints.txt`, then run:

```powershell
python -m pytest -q tests\unit
python -m pytest -q tests\integration -m "not performance"
python -m pytest -q
```

The automated suite uses the fake provider, mocked OpenAI SDK objects, or the
real pinned SDK with HTTP MockTransport. Azure tests cover multipart
PNG/JPEG/WebP, API-key-only authentication, endpoint rejection, bounded
conversion, and a spawned offline worker journey. No real credentials,
live OpenAI/Azure application calls, or model provisioning are needed.

Automated tests remain offline. `python -m pip_audit` is a separate release
check that requires advisory access but makes no OpenAI request. Run both
`python -m pip check` and `python -m pip_audit` in the final installation
environment before release.

When Python 3.11 is not installed, dependency compatibility can be checked
without claiming application execution:

```powershell
python -m pip install --dry-run --ignore-installed --only-binary=:all: `
  --platform win_amd64 --implementation cp --python-version 3.11 --abi cp311 `
  -c constraints.txt "openai==2.54.0" "Pillow==12.3.0" "PyYAML==6.0.3" `
  "pytest==9.1.1" "pip-audit==2.10.1"
```
