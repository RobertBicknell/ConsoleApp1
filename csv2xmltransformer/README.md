# Csv2Xml

Turns CSV files into XML that conforms to an XSD. A mapping (which CSV column feeds which XML item) is designed once,
stored by the host application as bytes, and later used to convert CSV files in production.

## Solution layout

| Project | Target | Role |
|---|---|---|
| `Csv2Xml.Runtime` | net9.0 | Production writer. Reads a stored mapping and CSVs, writes XML, validates, packages, sends. Knows nothing of design. |
| `Csv2Xml.Design` | net9.0 | Design-time library: reads the XSD, suggests and validates mappings, verifies samples, generates sample CSVs. References Runtime. |
| `Csv2Xml.SamplePlugin` | net9.0 | Example run-time plugin (`Transform(Context)`). |
| `Csv2Xml.TestUi` | net9.0 | Blazor Server test UI. Uses only the public API of Design/Runtime. Never shipped. |
| `Csv2Xml.Tests` | net9.0 | xUnit tests (185). |
| `Csv2Xml.TestPlugin` | net9.0 | Test-only plugin that misbehaves on request (throws, duplicate entry). |

Dependency rule: `Runtime` ← `Design` ← (`TestUi`, host application). The runtime never references design code.

Public surface a host needs: `MappingDesigner`, `SampleGenerator` (Design); `ConfigJson`, `RunPipeline`, `RunRequest`,
`JobSpec`/`MappingSpec`, output sinks, `Context` for plugins (Runtime). Matching and value-synthesis helpers are internal.

Shared build settings live in `Directory.Build.props`: net9.0, nullable, warnings as errors, deterministic builds,
version 1.0.0, XML documentation for the two libraries.

## Concepts

- **Kind**: one document type the XSD permits. All kinds share the same root element and XSD; the kind is the branch of the `choice` in the root that the file populates. The root is the first global element (file order, not used via `ref` by another element) whose content has a choice of two or more elements; its branches fill the kind dropdown, and the chosen branch element is written by the tool (it is never in the CSV). Other global elements are treated as helpers. With no such choice, each global element is a kind. Each kind is fed by one CSV. [EdgeCaseTests]
- **Target**: a value-bearing XML item (simple element or attribute), addressed by path from the kind root, e.g. `Orders/Order/Line/@sku`. Carries type, requiredness, repeating ancestors and XSD restrictions (`Facets`).
- **Run values** (root fields): document-level items outside the kinds (e.g. a submission id), written once per file inside the root before the kind. Never mapped from CSV. The mapping lists them (`MappingSpec.RootFields`); the job defines how each is supplied (`JobSpec.Parameters`: key, label, type, required, default token, created by `MappingDesigner.Job`); each run passes values by key in `RunRequest.Parameters`.
- **MappingSpec**: the stored mapping for one kind. Each target is bound to a **column index**; the header text found there at design time is recorded and re-checked at run time. Also records the XSD's content hash, namespaces, delimiter and CSV encoding.
- **JobSpec**: one MappingSpec per kind plus run-parameter definitions and packaging policy.

## Designing a mapping from a host application

All calls are on `Csv2Xml.Design.MappingDesigner` (pure functions; only `ReadHeaders` reads a stream you pass):

```csharp
var doc       = MappingDesigner.LoadSchema(xsdText);
var delimiter = MappingDesigner.DetectDelimiter(firstLineOfCsv);    // a guess: let the user confirm
var headers   = MappingDesigner.ReadHeaders(csvStream, delimiter);  // same parser/decoding as run time
var kind      = userChoice;                                         // the user says what the file is: one of MappingDesigner.Kinds(doc)
                                                                    // (RankKinds(doc, headers) can hint, never decide)
var targets   = MappingDesigner.Targets(doc, kind);                 // the XML item list (t.Path, t.Required, t.DataType)
var columns   = MappingDesigner.Columns(headers);                   // dropdown entries: (Index, DisplayName)
var initial   = MappingDesigner.Suggest(doc, kind, headers);        // path -> column index or null, to pre-fill dropdowns

// user edits dropdowns, then everything is submitted at once:
BuildResult r = MappingDesigner.Build(doc, kind, headers, assignments /* path -> column index */, delimiter, encoding: null);
if (!r.Success) show(r.Errors);                                     // all errors at once; r.Warnings never block
byte[] stored = ConfigJson.ToBytes(r.Spec!);                         // host persists this
```

- `DisplayName` is the header text, suffixed ` (#n)` (1-based) only when the header is blank or duplicated
  (trim, case-insensitive). It exists only for display; the mapping stores the index.
- `Build` errors: unknown kind or path, a run-value path, a column out of range, a required target with no column,
  assigned targets in sibling repeating groups. Warning: one column feeding several targets.
- `encoding`: null means UTF-8. Set e.g. `"windows-1252"` when the source system writes Excel-style ANSI CSV.
- `MappingDesigner.Verify(xsdText, spec, sampleCsv)` runs a sample through the real writer and validates the XML
  against the XSD; it returns the XML and every problem, never throws. Use it to prove a mapping before storing it.
- `MappingDesigner.LayoutProblems(spec, headers)` says whether a CSV's header row fits a stored mapping.
- `MappingDesigner.Job(name, mappings)` assembles a runnable `JobSpec` from stored mappings alone (no XSD). Its
  `Parameters` are the document-level values the user must supply per run: key, label, type, required, default
  (date-times `{now}`, dates `{today}`). Store it with `ConfigJson.ToBytes(job)`, or build it from the stored mapping
  when a file is processed. `MappingDesigner.RunParameters(doc)` gives the same definitions before a mapping exists.

Processing one file, as the host does it (the values form is built from `job.Parameters`):

```csharp
var job    = MappingDesigner.Job("weekly", [ConfigJson.MappingFromBytes(stored)]);
var values = new Dictionary<string, string> { ["submissionId"] = "S-1", ["SubmitterName"] = "Rob" }; // SubmittedAt blank -> {now}
var result = await new RunPipeline(new FileOutputSink(outDir)).RunAsync(job,
    new RunRequest(new() { [job.Mappings[0].Kind] = csvPath }, values, xsdText));
// a required value missing, a wrong type or an unknown key -> RunException listing every problem; nothing written
```

## Running

```csharp
var job    = ConfigJson.JobFromBytes(storedJob);
var result = await new RunPipeline(new FileOutputSink(outDir))        // or HttpPostOutputSink, or your own IOutputSink
    .RunAsync(job, new RunRequest(
        Inputs:     new Dictionary<string, string> { ["Orders"] = ordersCsvPath, /* one per kind */ },
        Parameters: new Dictionary<string, string> { ["submissionId"] = "S-1" },
        XsdText:    xsdText),                                         // optional; enables the XSD checks below
        pluginFolder: null);
```

Order of operations; nothing is written or sent unless every step succeeds:

1. Run values resolve (supplied, else default token `{now}` `{today}` `{guid}` `{machine}` `{user}`) and type-check. All problems reported together.
2. The supplied XSD (if any) compiles.
3. Preflight, for every input, all problems reported together:
   - the job has at most one mapping per kind, and every input belongs to a kind;
   - when `XsdText` is supplied, each mapping's recorded XSD hash must match it;
   - every input exists, decodes, and its header row still has the recorded header at each mapped index;
   - every required target and required run value is bound.
4. Each CSV is streamed to XML (memory independent of file size). Row checks apply (see edge cases); a failing row halts
   the run or is skipped (`ErrorPolicy`).
5. When `XsdText` is supplied and `ValidateOutput` is on, each XML is validated against it.
6. A report is built (rows read, written, skipped blank, row errors), the plugin (if any) may adjust files and report,
   and everything is zipped and sent as one package. The file sink writes to a temporary name and renames, and removes
   the partial file on failure.

## Edge cases

Each line is pinned by a test (file in brackets, under `tests/Csv2Xml.Tests`).

### CSV structure

| Case | Behaviour |
|---|---|
| Quoted fields with delimiters, doubled quotes `""`, embedded CR/LF | Parsed as data (RFC 4180). [EdgeCaseTests, CsvParserTests] |
| CR, LF or CRLF line endings, mixed | All accepted. [CsvParserTests] |
| Byte-order mark at the start of text passed as a string | Ignored. [EdgeCaseTests] |
| Row with more or fewer fields than the header (e.g. unquoted `Smith, John`) | Row error naming both counts; never a silent column shift. Headerless CSVs are not checked (no reference count). [EdgeCaseTests] |
| Quoted field never closed | Run halts: `line N: quoted field is never closed` (N = line where the record starts). [EdgeCaseTests] |
| Empty lines | Ignored by the parser. [EdgeCaseTests] |
| Rows whose fields are all empty/whitespace (Excel's trailing `,,,`) | Skipped and counted as blank in the report, not errors. [EdgeCaseTests] |
| Empty CSV / header only | Mapping error / document with an empty kind element (valid only if the XSD allows zero instances). [TransformerTests] |
| Characters XML cannot carry (control chars U+0000–U+001F except tab/CR/LF, U+FFFE/FFFF, unpaired surrogates) in data | Row error naming column and code point; previously an unhandled crash. In a run value: refused before output. Emoji and other surrogate pairs, accents, CJK are fine. [EdgeCaseTests] |
| `&`, `<`, `>`, quotes in data | Escaped by the XML writer. [TransformerTests] |
| Leading/trailing spaces in values | Kept as-is; XSD whitespace rules of the target type then apply. Whitespace-only counts as empty. |
| Required value empty in a row | Row error listing the missing paths. [TransformerTests] |
| Rows for one repeating instance not consecutive | Produce separate instances (grouping is by consecutive equal values). Known limit. |

### Headers and columns

| Case | Behaviour |
|---|---|
| Duplicate header names | Bound by index; display names `name (#3)`, `name (#4)`. [BindingTests, DesignerTests] |
| Blank header | Display `(blank) (#n)`; never suggested. [DesignerTests, SampleFileTests] |
| Header names differing only by case, surrounding spaces, non-breaking spaces, or a stray BOM | Treated as equal. [EdgeCaseTests] |
| Columns reordered, inserted or removed since design | Run refused before output: `column N expected header 'x', found 'y'`, every moved column listed. [BindingTests] |
| Header row shorter than the mapping | Refused: `maps to column N, but the CSV has only M columns`. [BindingTests] |
| Literal header text equal to a generated display name (`A (#2)`) | Display names stay unique. [DesignerTests] |

### Encodings

| Case | Behaviour |
|---|---|
| UTF-8 without BOM | Default. |
| UTF-8 / UTF-16 LE / UTF-16 BE with BOM | Detected from the BOM; the BOM wins over a declared encoding. [EdgeCaseTests] |
| Bytes invalid in the declared encoding (e.g. a Windows-1252 file read as UTF-8) | Run refused / halted with a message suggesting `Encoding`; previously silently replaced by U+FFFD. [EdgeCaseTests] |
| Windows-125x, ISO-8859-x, other code pages | Supported by naming them in the mapping's `Encoding`. [EdgeCaseTests] |
| Unknown encoding name | Preflight problem. [PipelineTests] |
| Delimiter `,` `;` tab `\|` | Any single character works; `DetectDelimiter` guesses among these, ignoring quoted text. [EdgeCaseTests] |
| XML output | Always UTF-8 without BOM. No `<?xml version encoding?>` declaration by default (`JobSpec.OmitXmlDeclaration = true`); set it false to emit one, which then says `utf-8`. `MappingDesigner.Verify(..., omitXmlDeclaration)` mirrors it. A stored job without the setting reads as true. [PipelineTests, DesignerTests] |

### XSD

| Case | Behaviour |
|---|---|
| `targetNamespace` with qualified local elements | All elements in the namespace. [EdgeCaseTests] |
| `targetNamespace` with unqualified local elements (the XSD default) | Root in the namespace, local elements unqualified; previously the namespace was dropped and output was invalid. [EdgeCaseTests] |
| Local elements mixing qualified and unqualified forms (per-element `form`) | Refused at load with a clear message. [EdgeCaseTests] |
| `xs:include` / `xs:import` / `xs:redefine` | Refused at load and at run time; previously silently ignored. Merge into one self-contained XSD. [EdgeCaseTests] |
| DTD inside the XSD | Refused (DTD processing prohibited); no external resources are ever fetched. [EdgeCaseTests] |
| Same XSD with/without BOM, CRLF vs LF | Same hash, so stored mappings stay valid across machines. [ValidationTests] |
| Any content change to the XSD | Different hash; runs and `Verify` refuse mappings built against the old one. [ValidationTests, PipelineTests] |
| Restrictions: enumeration, pattern, length/min/max, ranges, digits, fixed, derived named types, simpleContent | Read into `Facets`; used by the sample generator; enforced by XSD validation. [FacetTests] |
| Choice inside a kind | Its branches are optional targets; filling more than one branch fails XSD validation. |
| Sibling repeating groups (Order/Line and Order/Note) | Either group may be mapped, not both together (one CSV row cannot express both). `Build` reports it; the generator demonstrates the main chain and notes the other. [EdgeCaseTests] |
| Nesting deeper than 24 levels / recursive types | Walk stops at depth 24. |
| `xs:any`, mixed content, nillable, substitution groups | Not mapped (no value-bearing target is created). |

### Stored configuration

| Case | Behaviour |
|---|---|
| Wrong format (job vs mapping) or unknown version | `InvalidDataException`. [ConfigJsonTests] |
| Missing or null required members (e.g. no `fields`) | `InvalidDataException` on load, not a later null reference. [EdgeCaseTests] |
| Configs saved before `rootNamespace`/`localNamespace`/`encoding` existed | Load; missing values default to the kind namespace and UTF-8. |

### Runs and delivery

| Case | Behaviour |
|---|---|
| Two mappings for one kind in a job | Preflight problem. [EdgeCaseTests] |
| Plugin adds a file whose name duplicates another package entry (case-insensitive) | Run refused (no duplicate zip entries). [PipelineTests] |
| Plugin throws | `RunException("plugin failed: ...")`; nothing sent. [PipelineTests] |
| Sink copy fails midway | No partial file left; exception propagates. [EdgeCaseTests] |
| Cancellation | Honoured between rows and inputs; nothing sent [PipelineTests]. The temp folder is removed in a `finally` block (not asserted by a test). |
| Large inputs | Streamed; tested at 300,000 rows within a fixed memory bound. [TransformerTests] |

## Stored format

UTF-8 JSON in a versioned envelope: `{"format":"csv2xml.mapping"|"csv2xml.job","version":1,"content":{...}}`.

## Generating sample CSVs from an XSD

```csharp
SampleSet s = SampleGenerator.Generate(xsdText, exemplarXml /* optional */);
s.WriteTo(folder);     // or s.ToFiles(): file name -> bytes; s.Files, s.Notes, s.AllPassed, s.Readme()
```

For each kind it writes `Kind.scenario.csv`, the correct `Kind.scenario.mapping.json`, `Kind.scenario.expected.xml`
(valid scenarios) and a `README.md` with PASS/FAIL per file. Scenarios: `clean`, `noisy`, `hierarchy`, `optional_empty`,
`duplicate_headers`, `abbreviated` (all expected valid), `reordered` (expected layout error), `bad_data` (expected row error).
Every file is proven by running it through the writer and XSD validation. `WriteTo` overwrites same-named files and
leaves others in the folder untouched; use an empty folder.

Two modes; neither ever puts another kind's items into a CSV:

- **With an exemplar** (a real document): generates the **one kind the exemplar contains** (`s.ExemplarKind`). The exemplar
  must be valid against the XSD, else `InvalidDataException` listing the problems. That catches a missing mandatory item,
  a wrong root, or a root that is not one of the XSD's kinds. **Every value comes from the exemplar; nothing is invented.**
  Items the exemplar does not contain (optional ones) are left out of the CSVs and listed in the notes. A kind with no
  mappable values, or one whose required items need two independent repeating groups, is an error.
- **XSD only**: generates **every kind**, each file holding only its own kind's items. With no real document to draw on,
  values are synthesised from the XSD types and restrictions (enumeration, fixed, length, ranges, digits, simple regex
  patterns). A pattern that cannot be synthesised with certainty is reported in the notes. A kind that cannot be
  generated is skipped with a note, so the other kinds are still produced.

## Security notes

- XSDs and exemplars are parsed with DTDs prohibited and no resolver: no entity expansion, no network or file access.
- The plugin folder is trusted code: the first `Transform(Context)` found there runs in-process with the host's rights.
- The runtime reads only the input paths given in the request and writes only to a temp folder and the sink.

## Test UI

`start-ui.cmd` (or `dotnet run --project src/Csv2Xml.TestUi`) then http://localhost:5190. It exercises the whole
product end to end through the public API:

1. **XSD and CSV**: load (delimiter detected, encoding optional).
2. **Kind of this file**: the user chooses, as in production; nothing is pre-selected. The UI says which element the
   tool writes for it.
3. **Document-level values**: the run values (key, type, default), with the values to use.
4. **Assign columns**: pre-filled by suggestion, then Build.
5. **Process a file**: **Run** is the production path (`MappingDesigner.Job` + `RunPipeline`: preflight, transform,
   XSD validation, report, zip into the output folder), showing the report and the XML. A missing required value or a
   bad file is listed and nothing is written. **Preview XML only** is the quick in-memory check (`Verify`).
6. **Mapping file**: save or load the stored mapping.
7. **Generate sample CSVs**, each with an "open in mapper" button that loads it with its kind chosen.

Sample inputs are in `samples/`; `samples/csv/README.md` describes what each CSV demonstrates.

- Dark theme by default; ◐ toggles light/dark (remembered per browser).
- File pickers browse the server's file system (the UI runs locally, so real paths are available, which a browser file
  input cannot give). Each opens in the folder of its current value; last-used paths are remembered in
  `%LOCALAPPDATA%\Csv2Xml\ui-settings.json`. Defaults: `samples/` in a checkout, else `Documents\Csv2Xml`; mappings go to
  `Documents\Csv2Xml\mappings\<Kind>.mapping.json`, run output to `Documents\Csv2Xml\output`, generated samples to
  `Documents\Csv2Xml\generated`. Under each box the file name is shown in full beside its folder; browsing shows
  clickable breadcrumbs.
- 👁 opens a file or result in a new window: XML/XSD and JSON as coloured trees with +/− folding (expand/collapse all,
  open 1–3 levels, find, raw), CSV as a table read with the same parser, delimiter and encoding as the runtime, with
  column numbers, duplicate/blank headers flagged, rows with the wrong field count highlighted, and a toggle that shows
  spaces, tabs, NBSP and BOM. Previews are served only from content the UI registered (random id), never from a path in the URL.

## Known limits

- One chain of repeating elements per mapping (see sibling repeating groups above).
- Suggestions are lexical (case, separators, typos, parent-qualified names). Abbreviations and synonyms (`Qty`, `Town`)
  are left for the user to assign.
- Rows belonging to one repeating instance must be consecutive in the CSV.
- Single self-contained XSD only (no include/import).
- Local elements must all be qualified or all unqualified.
- A further `choice` among the document-level values (e.g. Email *or* Phone) is not resolved by the tool: each branch is an
  optional run value; supply the one you want. Verify fills only required run values with placeholders, as a run would.

## Build and test

```
dotnet build
dotnet test
```
