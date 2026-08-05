# Plan: Receipt scanning — filling the inventory from a photo or PDF

Status: implemented (2026-08-05)
Scope: reading the grocery lines off a till receipt, reviewing them, and
upserting the confirmed ones.
Explicitly **out of scope** (future work): receipt history or re-scanning a
stored one; prices, totals or spend tracking; shopping lists; deducting what a
receipt says was eaten; MCP exposure of the scanner.

## Context

Stocking the kitchen after a shop means typing every item into `/inventory` by
hand, and the household already photographs receipts. A scan removes the typing
without removing the judgement — which has to stay with a person, because a
receipt is a poor description of a pantry:

- names are till abbreviations (`LAIT DEMI-ECR 1L`);
- a third of the lines are not food — carrier bags, batteries, the deposit;
- `UpsertAsync` **replaces** `Quantity`, and quantity here is free text with no
  honest way to add "3 sachets" to "1 kg". A receipt for a second bag of rice
  would silently overwrite what was there.

So the shape is: **the scan proposes, a person confirms, `InventoryService`
writes.** Nothing about the receipt is persisted — no table, no migration, no
file on disk at any point.

## What was measured first

The design question was how to get a picture to the model without giving up the
flag set `ClaudeIngredientClassifier` is careful about. `--input-format
stream-json` takes a base64 `image` (or `document`) content block on stdin,
which keeps `--tools ""` true; the alternative was writing the upload to a temp
directory and handing the model the `Read` tool.

Four runs against printed receipts, all one-shot correct unless noted:

| Input | Wall clock | Note |
|---|---|---|
| PNG 520×404 | 11.4s | burned a turn on the `items` schema name, below |
| PDF, image-only (no text layer) | 6.3s | |
| JPEG 3024×4032, 2.05 MB | 8.0s | phone-camera sized |
| JPEG 3024×4032, 4.47 MB | 7.3s | |

Both CLI runs reported `"tools":["StructuredOutput"]` on the `init` line —
nothing else was reachable. Three findings came out of this and are in the code
where they bite:

- **the schema's array must not be called `items`.** Named that, the model
  answered `{"items":{"items":[…]}}` — the schema's own array keyword was in
  front of it — the response was rejected and it spent a turn recovering.
  `products` was right first time.
- **the schema needs an `isFood` flag.** Every receipt carries bags and
  batteries, and the model judges this well when asked. Measured: `Sac
  plastique` and `Piles AAA` both came back `false`, `Pois chiches` `true`.
- **nothing needs to resize a photograph.** The 2 MB and 4.47 MB runs above are
  what killed a planned `wwwroot/receipt.js` canvas re-encode: the CLI handles
  the shrinking, so the JS file, its interop call and a stubbed call in every
  page test would have bought nothing. `MaxBytes` is the whole size story.

## Deliverables

### 1. `ReceiptScanningOptions` + the `ReceiptScanning` config section

Third of a set beside `Categorization` and `RecipeGeneration`. `Effort` is
`low` like the classifier's — transcription, not deliberation.
`TimeoutSeconds` 120 sits between their 90 and 180. `MaxBytes` 5 MB matches the
API's own per-image limit; `MaxLines` 60 caps a review table nobody could read.

### 2. `IReceiptScanner`, `ReceiptFile`, `ScannedLine`

`ReceiptFile` carries bytes, never a path — the upload goes from the browser
straight to the subprocess's stdin, so no receipt touches this machine's disk.
`ReceiptFile.IsSupportedMediaType` is an allow-list; HEIC is the notable
absence, and the page refuses it with a message rather than letting the CLI
reject it six seconds later.

Same contract as the other two services: **implementations must not throw.** An
empty list is the whole failure signal.

### 3. `ClaudeReceiptScanner`

Mirrors `ClaudeIngredientClassifier`'s process handling exactly — `ArgumentList`,
temp `WorkingDirectory`, drain-both-pipes-before-writing, timeout and kill. It
adds `--input-format stream-json --output-format stream-json --verbose`, which
travel as a set: stream-json input needs the matching output format, which needs
`--verbose`. Two pure functions carry the logic worth testing: `BuildPayload`
(image vs document block) and `ParseScan`.

`ParseScan` looks for the `{"type":"result"}` line rather than the first `{` in
the stream, unlike the other two parsers. It has to: the assistant's own turn
appears earlier and can hold the shape the schema *rejected*.

### 4. The scan card on `/inventory`

Below the add form — deliberately, and the page's own tests depend on it, since
several reach for `Find("button.btn-primary")` and that has to keep meaning Add.
Four states in one card: idle, scanning (with Cancel), review, error.

- **the review rows live in `@code` fields, never in the DOM**, the same rule
  the row editor's draft follows and for a sharper reason: confirming writes one
  row at a time and every write publishes, so a refresh and a re-render land
  between one row and the next.
- **the effect badge is derived on every render** from
  `IngredientMatcher.ExactMatch`, so a row another tab creates mid-review flips
  it from New to Replaces on its own. For a match it shows the old quantity
  beside the new one — that badge is the only warning before an overwrite.
- **non-food lines arrive unticked, not dropped.** A greyed row costs one click
  to disagree with; a missing one leaves no recourse.
- **no category travels with a scanned line.** The upsert passes `null`, which
  lands a new row in `Other` and leaves an existing row's category alone, so
  `IngredientCategorizer` keeps owning classification — cache and batching
  included. Verified live: five confirmed rows sorted themselves into Produce,
  Fresh Herbs, Dairy, Canned and Grains in one 6.3s batch, and `Sac plastique`
  correctly stayed in `Other`.
- **`role="alert"` for scan errors**, never a second `role="status"` — the add
  form owns this page's one status region.
- **cancel is answered twice**: by the token, and by an
  `IsCancellationRequested` check after the await. A scan that finished while
  the click was in flight comes back with real lines and no exception to catch,
  and opening a review out of one the user just called off is the same bug as
  ignoring the button. The page test found this.
- the `<InputFile>` is `@key`ed on a counter so it is a fresh element after
  every scan: a file input fires no change event when the same file is picked
  twice, which would read as a dead button on a re-scan.
- **no `capture="environment"`**, which the mockups asked for and which turns
  out to be backwards. It reads as the mobile-friendly choice; what it actually
  does is open the camera *instead of* the picker, so a receipt photographed an
  hour ago becomes unreachable. Without it a phone offers camera and gallery
  both, which is what the two buttons in the mockup meant.

### 5. Tests

`ReceiptParsingTests` covers the two pure functions over fixtures shaped like a
real stream-json run. `ReceiptScanTests` drives the page through `PageHarness`
with a `FakeReceiptScanner`. No process is spawned by either.

Two things worth knowing before adding to them:

- **bUnit's `UploadFiles` blocks until the handler it triggers completes.** The
  gated cancel test therefore calls it on its own thread — inline, there is no
  thread left to click Cancel with and the test hangs instead of failing.
- the review is a `<ul>`, not a `<table>`. The page's one `<table>` is the
  inventory, and existing tests select inside `tbody` to find the row editor; a
  second tbody full of inputs would make every one of those ambiguous whenever
  a receipt was open.

Guards verified to bite by breaking them: the `Keep` filter on confirm, the
`Keep = line.IsFood` default, the cancellation check, and the null category.
Each turned exactly the expected test red.
