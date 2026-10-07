# Feature 15 — CSV Import Backend

**Branch:** `feature/csv-import-backend`
**Sprint:** 1
**Depends on:** F11 (`LeadIngestionService` — all dedupe/validation lives there), F14.0 (Hangfire, `IJobScheduler`, `FakeLeadSource` is irrelevant here).

## Goal
Upload a CSV, preview it, map columns, and import it in a background job with per-row results — safely at 50k rows. This is the highest-risk file-handling feature, so it is **test-first**: write the reader/mapper tests from §Tests before the implementation. Nothing here re-implements validation or dedupe; rows go to `LeadIngestionService`.

## Design decisions (recorded here)
- **Where things live.** CsvHelper is an Infrastructure package (parent plan §11), so the reader sits behind `ICsvRowReader` (Application) and is implemented in `Infrastructure/Csv`. The *mapping* of a row to a `CandidateLead` is pure Application code and unit-tested without CsvHelper.
- **Mapping by header name, not column index**, stored as JSON on the batch, so a re-run or an error report still makes sense.
- **Row numbers** in errors are 1-based *data record* numbers (the header is row 0). A record containing embedded newlines counts once. The error report says so.
- **Resumability by checkpoint**: the batch stores `ProcessedRowCount`; a retried job skips that many records and carries on. Leads already inserted would be reported `Duplicate` anyway, so a crash can never create duplicates — the checkpoint only keeps the counters honest.
- **Errors are kept in the DB, capped** (first 1,000 non-imported rows as JSON), and the downloadable CSV is generated from that. `ErrorCount` always holds the true total.
- **Encoding**: BOM wins; otherwise strict UTF-8, falling back to Latin-1 for old Excel exports. **Delimiter**: sniffed from the header line among `, ; \t |`.

## Files to add/modify

### Packages
`ZenLead.Infrastructure`: `CsvHelper`, `Azure.Storage.Blobs`.

### Domain

**`ZenLead.Domain/Entities/CsvImportBatch.cs`** (new) + **`Enums/CsvImportStatus.cs`**
```csharp
public enum CsvImportStatus { Uploaded, Parsing, Completed, Failed }

public class CsvImportBatch : ITenantEntity
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string FileName { get; set; } = string.Empty;       // display only; never used to build a path
    public string BlobPath { get; set; } = string.Empty;       // imports/{workspaceId}/{batchId}.csv
    public string? ColumnMappingJson { get; set; }
    public string Delimiter { get; set; } = ",";               // detected at upload
    public int RowCount { get; set; }                          // data records, counted at upload
    public int ProcessedRowCount { get; set; }                 // checkpoint
    public int ImportedCount { get; set; }
    public int SkippedDuplicateCount { get; set; }
    public int SkippedSuppressedCount { get; set; }
    public int ErrorCount { get; set; }                        // invalid rows
    public string? ErrorLogJson { get; set; }                  // capped list of CsvRowIssue
    public string? FailureReason { get; set; }
    public CsvImportStatus Status { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
```

### Application

**`ZenLead.Application/Abstractions/IBlobStorage.cs`** (new)
```csharp
public interface IBlobStorage
{
    Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default);   // null if missing
    Task DeleteAsync(string path, CancellationToken ct = default);
}
```

**`ZenLead.Application/Abstractions/ICsvRowReader.cs`** (new)
```csharp
public record CsvRow(int Number, IReadOnlyDictionary<string, string> Fields);          // Number: 1-based data record
public record CsvPreview(string Delimiter, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> SampleRows, int RowCount);

public class CsvFormatException(string message) : Exception(message);                  // not a CSV / no header / binary / too many columns

public interface ICsvRowReader
{
    /// <summary>Reads the whole stream once: validates it is text CSV, detects delimiter, counts data records, returns the first rows.</summary>
    Task<CsvPreview> PreviewAsync(Stream stream, int sampleRows, int maxRows, CancellationToken ct = default);

    /// <summary>Streams data records. Malformed records come back with <see cref="CsvRow.Fields"/> empty and an entry in <c>errors</c> rather than throwing.</summary>
    IAsyncEnumerable<CsvRowOrError> ReadAsync(Stream stream, string delimiter, CancellationToken ct = default);
}
public record CsvRowOrError(int Number, CsvRow? Row, string? Error);
```

**`ZenLead.Application/Csv/CsvImportModels.cs`** (new)
```csharp
public record ColumnMapping(string? Name, string? FirstName, string? LastName, string Email,
    string? Title, string? CompanyName, string? CompanyDomain, string? Industry, string? Country, string? CompanySize);

public record CsvRowIssue(int Row, IngestionOutcome Outcome, string Reason, string? Email, string? Name);
```

**`ZenLead.Application/Csv/CsvRowMapper.cs`** (new, pure)
```csharp
public static class CsvRowMapper
{
    public static CandidateLead Map(IReadOnlyDictionary<string, string> fields, ColumnMapping m)
    {
        string? Get(string? header) => header is not null && fields.TryGetValue(header, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var name = Get(m.Name) ?? string.Join(' ', new[] { Get(m.FirstName), Get(m.LastName) }.Where(s => s is not null));
        return new CandidateLead(name, Get(m.Email), Get(m.Title), Get(m.CompanyName), Get(m.CompanyDomain),
                                 Get(m.Industry), Get(m.Country), Get(m.CompanySize));
    }

    /// <summary>Returns problems with the mapping itself (missing email, unknown headers, same header mapped twice for different fields).</summary>
    public static IReadOnlyList<string> Validate(ColumnMapping m, IReadOnlyCollection<string> headers) { /* … */ }
}
```
`Validate` rules: `Email` required and present in `headers` (case-insensitive match; the controller normalises the mapping to the file's exact header spelling); every non-null mapped header exists; no header mapped to two different fields except `Name` + `FirstName/LastName` exclusivity (if `Name` is set, `FirstName`/`LastName` must be null).

**`ZenLead.Application/UseCases/Leads/Import/`** (new folder)

`CreateCsvImportUseCase` — **upload & preview** (15.2)
```csharp
public class CreateCsvImportUseCase(IBlobStorage blobs, ICsvRowReader reader, ICsvImportRepository batches, TimeProvider clock)
{
    public const int MaxRows = 50_000, SampleRows = 10;

    public async Task<CsvUploadResult> ExecuteAsync(Guid workspaceId, Guid userId, string fileName, Stream file, CancellationToken ct)
    {
        var batchId = Guid.NewGuid();
        var path = $"imports/{workspaceId}/{batchId}.csv";

        // Reader needs two passes (preview + later job), so buffer once to blob first, then preview from the blob.
        await blobs.SaveAsync(path, file, "text/csv", ct);
        await using var stored = await blobs.OpenReadAsync(path, ct) ?? throw new InvalidOperationException("Blob not found after save.");

        CsvPreview preview;
        try { preview = await reader.PreviewAsync(stored, SampleRows, MaxRows, ct); }
        catch (CsvFormatException) { await blobs.DeleteAsync(path, ct); throw; }        // don't keep rejected files

        await batches.AddAsync(new CsvImportBatch
        {
            Id = batchId, WorkspaceId = workspaceId, FileName = Path.GetFileName(fileName), BlobPath = path,
            Delimiter = preview.Delimiter, RowCount = preview.RowCount, Status = CsvImportStatus.Uploaded,
            CreatedBy = userId, CreatedAt = clock.GetUtcNow().UtcDateTime
        }, ct);
        return new CsvUploadResult(batchId, preview.Headers, preview.SampleRows, preview.RowCount);
    }
}
```
`PreviewAsync` throws `CsvFormatException` for: empty file, header-only (0 records → "The file has no data rows"), NUL bytes in the first 4 KB (binary), > 100 columns, header row with duplicate/blank names (message names them), `RowCount > maxRows` ("more than 50,000 rows — split the file").

`StartCsvImportUseCase` — **mapping & start** (15.3)
```csharp
public async Task<StartImportResult> ExecuteAsync(Guid workspaceId, Guid batchId, ColumnMapping mapping, CancellationToken ct)
{
    var batch = await batches.GetAsync(batchId, ct);
    if (batch is null || batch.WorkspaceId != workspaceId) return StartImportResult.NotFound;
    if (batch.Status != CsvImportStatus.Uploaded) return StartImportResult.AlreadyStarted;      // start is once-only

    await using var stream = await blobs.OpenReadAsync(batch.BlobPath, ct);
    var headers = (await reader.PreviewAsync(stream!, 0, MaxRows, ct)).Headers;
    var problems = CsvRowMapper.Validate(mapping, headers);
    if (problems.Count > 0) return StartImportResult.InvalidMapping(problems);

    batch.ColumnMappingJson = JsonSerializer.Serialize(mapping, Web);
    batch.Status = CsvImportStatus.Parsing; batch.StartedAt = clock.GetUtcNow().UtcDateTime;
    await batches.UpdateAsync(batch, ct);
    scheduler.EnqueueCsvImport(batch.Id, workspaceId);
    return StartImportResult.Started;
}
```
(Re-reading headers server-side means the mapping is validated against the *stored file*, not what the client claims.)

`ProcessCsvImportUseCase` — the job body (15.4/15.5)
```csharp
public class ProcessCsvImportUseCase(ICsvImportRepository batches, IBlobStorage blobs, ICsvRowReader reader,
                                     LeadIngestionService ingestion, TimeProvider clock)
{
    public const int BatchSize = 500, MaxLoggedIssues = 1000;

    public async Task ExecuteAsync(Guid batchId, Guid workspaceId, CancellationToken ct)
    {
        var batch = await batches.GetForJobAsync(batchId, workspaceId, ct);                 // ForWorkspace — no HTTP context
        if (batch is null || batch.Status is CsvImportStatus.Completed) return;           // idempotent
        var mapping = JsonSerializer.Deserialize<ColumnMapping>(batch.ColumnMappingJson ?? "", Web);
        if (mapping is null) { await FailAsync(batch, "Column mapping missing.", ct); return; }

        await using var stream = await blobs.OpenReadAsync(batch.BlobPath, ct);
        if (stream is null) { await FailAsync(batch, "Uploaded file is no longer available. Upload it again.", ct); return; }

        batch.Status = CsvImportStatus.Parsing; batch.FailureReason = null;
        var issues = CsvIssueLog.Load(batch.ErrorLogJson, MaxLoggedIssues);
        var pending = new List<(int Number, CandidateLead Candidate)>(BatchSize);
        var skip = batch.ProcessedRowCount;                                                // resume point

        await foreach (var item in reader.ReadAsync(stream, batch.Delimiter, ct))
        {
            if (item.Number <= skip) continue;
            if (item.Row is null) { batch.ErrorCount++; issues.Add(new(item.Number, IngestionOutcome.Invalid, item.Error ?? "Malformed row", null, null)); batch.ProcessedRowCount = item.Number; continue; }

            pending.Add((item.Number, CsvRowMapper.Map(item.Row.Fields, mapping)));
            if (pending.Count == BatchSize) await FlushAsync(batch, workspaceId, pending, issues, ct);
        }
        if (pending.Count > 0) await FlushAsync(batch, workspaceId, pending, issues, ct);

        batch.Status = CsvImportStatus.Completed; batch.FinishedAt = clock.GetUtcNow().UtcDateTime;
        batch.ErrorLogJson = issues.ToJson();
        await batches.UpdateAsync(batch, ct);
    }

    private async Task FlushAsync(CsvImportBatch batch, Guid workspaceId, List<(int Number, CandidateLead Candidate)> pending, CsvIssueLog issues, CancellationToken ct)
    {
        var summary = await ingestion.IngestAsync(workspaceId, pending.Select(p => p.Candidate).ToList(),
                                                  new IngestionSource(LeadSource.Csv, batch.Id), ct);
        foreach (var r in summary.Rows.Where(r => r.Outcome != IngestionOutcome.Imported))
        {
            var (number, c) = pending[r.Index];
            issues.Add(new(number, r.Outcome, r.Reason ?? r.Outcome.ToString(), c.Email, c.Name));
        }
        batch.ImportedCount += summary.Imported;
        batch.SkippedDuplicateCount += summary.Duplicates;
        batch.SkippedSuppressedCount += summary.Suppressed;
        batch.ErrorCount += summary.Invalid;
        batch.ProcessedRowCount = pending[^1].Number;
        batch.ErrorLogJson = issues.ToJson();
        pending.Clear();
        await batches.UpdateAsync(batch, ct);                                              // one commit per batch → progress is visible and resumable
    }
}
```
`CsvIssueLog` (small helper): keeps the first `MaxLoggedIssues` entries, serialises with Web defaults. `FailAsync` sets `Status=Failed`, `FailureReason`, `FinishedAt`.

`GetCsvImportStatusUseCase` / controller code (15.6): `ImportStatusResponse(Id, FileName, Status, RowCount, ProcessedRowCount, ImportedCount, SkippedDuplicateCount, SkippedSuppressedCount, ErrorCount, FailureReason, Percent)`.

**`ZenLead.Application/Abstractions/ICsvImportRepository.cs`** (new) — `AddAsync`, `GetAsync(id)` (HTTP, filter-scoped), `GetForJobAsync(id, workspaceId)` (`ForWorkspace`), `UpdateAsync`. Add `void EnqueueCsvImport(Guid batchId, Guid workspaceId)` to **`IJobScheduler`** (+ `HangfireJobScheduler`, `NoopJobScheduler`).

**`ZenLead.Application/Csv/ErrorCsvWriter.cs`** (new) — builds the downloadable report: header `row,outcome,reason,email,name`; RFC-4180 quoting; **spreadsheet-formula guard**: any cell starting with `=`, `+`, `-`, `@`, tab or CR is prefixed with `'` (a hostile CSV must not turn our error report into a formula-injection vector when opened in Excel). First line comment-free; the controller adds a final line "Only the first 1000 issues are listed" row when `ErrorCount + dupes + suppressed > logged`.

### Infrastructure

**`ZenLead.Infrastructure/Csv/CsvHelperRowReader.cs`** (new)
```csharp
public class CsvHelperRowReader : ICsvRowReader
{
    private static readonly char[] Candidates = [',', ';', '\t', '|'];
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public async Task<CsvPreview> PreviewAsync(Stream stream, int sampleRows, int maxRows, CancellationToken ct = default)
    {
        var buffer = await ReadHeadAsync(stream, 4096, ct);                       // NUL check + encoding probe + delimiter sniff
        if (buffer.Contains((byte)0)) throw new CsvFormatException("That doesn't look like a text CSV file.");
        stream.Position = 0;
        var encoding = DetectEncoding(buffer);
        var delimiter = SniffDelimiter(encoding.GetString(buffer));

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var csv = new CsvReader(reader, Config(delimiter));
        if (!await csv.ReadAsync() ) throw new CsvFormatException("The file is empty.");
        csv.ReadHeader();
        var headers = csv.HeaderRecord!.Select(h => h.Trim()).ToList();
        ValidateHeaders(headers);

        var sample = new List<IReadOnlyList<string>>(); var count = 0;
        while (await csv.ReadAsync())
        {
            count++;
            if (count > maxRows) throw new CsvFormatException($"The file has more than {maxRows:N0} rows. Split it and import in parts.");
            if (sample.Count < sampleRows) sample.Add(headers.Select((_, i) => csv.TryGetField(i, out string? v) ? v ?? "" : "").ToList());
        }
        if (count == 0) throw new CsvFormatException("The file has a header but no data rows.");
        return new CsvPreview(delimiter.ToString(), headers, sample, count);
    }

    public async IAsyncEnumerable<CsvRowOrError> ReadAsync(Stream stream, string delimiter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // same encoding detection as above; config: BadDataFound → recorded, MissingFieldFound = null, TrimOptions.Trim
        // loop: try { if (!await csv.ReadAsync()) yield break; } catch (CsvHelperException ex) { yield return new(n, null, "Malformed quoting"); continue; }
        // yield new CsvRowOrError(n, new CsvRow(n, headers.ToDictionary(h => h, h => csv.GetField(h), StringComparer.OrdinalIgnoreCase)), null)
    }

    private static CsvConfiguration Config(char delimiter) => new(CultureInfo.InvariantCulture)
    {
        Delimiter = delimiter.ToString(), HasHeaderRecord = true, TrimOptions = TrimOptions.Trim,
        MissingFieldFound = null, HeaderValidated = null, BadDataFound = null,   // BadDataFound handled per row in ReadAsync via a flag
        DetectDelimiter = false, PrepareHeaderForMatch = a => a.Header.Trim()
    };
}
```
`SniffDelimiter` counts each candidate in the first non-empty line **outside quotes**, picks the highest count, defaults to `,`. Duplicate header names are rejected in `ValidateHeaders` (the dictionary-per-row model needs unique names; message lists them). > 100 columns rejected.
> **Verify** against the installed CsvHelper version: `BadDataFound` callback shape and whether a `ParserException` can be recovered from with a subsequent `ReadAsync()` (it can in v30+ for most bad-quote cases; if not, mark the rest of the file `Failed` with the row number rather than looping).

**`ZenLead.Infrastructure/Storage/AzureBlobStorage.cs`** (new) — `BlobServiceClient`-based; container `zenlead` (created on first use, private).
```csharp
public class AzureBlobStorage(BlobServiceClient service) : IBlobStorage
{
    private const string Container = "zenlead";
    private async Task<BlobContainerClient> ContainerAsync(CancellationToken ct)
    {
        var c = service.GetBlobContainerClient(Container);
        await c.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        return c;
    }
    public async Task SaveAsync(string path, Stream content, string contentType, CancellationToken ct = default)
        => await (await ContainerAsync(ct)).GetBlobClient(path).UploadAsync(content, new BlobUploadOptions { HttpHeaders = new() { ContentType = contentType } }, ct);
    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var blob = (await ContainerAsync(ct)).GetBlobClient(path);
        return await blob.ExistsAsync(ct) ? await blob.OpenReadAsync(cancellationToken: ct) : null;
    }
    public async Task DeleteAsync(string path, CancellationToken ct = default)
        => await (await ContainerAsync(ct)).GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);
}
```
`Program.cs`: `BlobServiceClient` singleton from `Storage:ConnectionString` (dev: `UseDevelopmentStorage=true` → Azurite) **or**, when `Storage:AccountUri` is set, `new BlobServiceClient(new Uri(uri), new DefaultAzureCredential())` (F30 flips config only). `OpenReadAsync` on a seekable-less network stream: `PreviewAsync` does `stream.Position = 0` — `BlobClient.OpenReadAsync` returns a seekable stream; confirm in the test with Azurite.

**`ZenLead.Infrastructure/Persistence/`** — `CsvImportBatchConfiguration` (`FileName` 260, `BlobPath` 300, `Delimiter` 8, `FailureReason` 500, JSON columns `nvarchar(max)`, index `(WorkspaceId, CreatedAt)`), `DbSet<CsvImportBatch>`, `CsvImportRepository`. Migration **`AddCsvImportBatch`**.

**`ZenLead.Infrastructure/Jobs/ProcessCsvImportJob.cs`** (new)
```csharp
public class ProcessCsvImportJob(ProcessCsvImportUseCase useCase, ICsvImportRepository batches, ILogger<ProcessCsvImportJob> logger)
{
    [DisableConcurrentExecution(timeoutInSeconds: 900)]
    public async Task ExecuteAsync(Guid batchId, Guid workspaceId, CancellationToken ct)
    {
        try { await useCase.ExecuteAsync(batchId, workspaceId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "CSV import {BatchId} failed", batchId);
            await batches.MarkFailedAsync(batchId, workspaceId, "Unexpected error while importing. Retrying…", CancellationToken.None);
            throw;                                                  // Hangfire retries (3×); the checkpoint makes the retry resume, not restart
        }
    }
}
```
(`MarkFailedAsync` flips the status so the UI never spins forever if retries are exhausted; the use case sets `Parsing` again on the next attempt.)

### Api

**`ZenLead.Api/Controllers/V1/LeadImportController.cs`** (new) — `[Authorize] [Route("api/v1/leads/import")]`
```csharp
[HttpPost]
[RequestSizeLimit(11 * 1024 * 1024)]                       // 10 MB file + form overhead; Kestrel's 30 MB default is too generous
[RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
public async Task<ActionResult<CsvUploadResponse>> Upload(IFormFile file, CancellationToken ct)
{
    if (this.WorkspaceId() is not { } workspaceId || this.UserId() is not { } userId) return Unauthorized();
    if (file is null || file.Length == 0) return Problem("No file uploaded.", statusCode: 400);
    if (file.Length > 10 * 1024 * 1024) return Problem("File is larger than 10 MB.", statusCode: 413);
    if (!string.Equals(Path.GetExtension(file.FileName), ".csv", StringComparison.OrdinalIgnoreCase)) return Problem("Only .csv files are accepted.", statusCode: 400);
    try
    {
        await using var stream = file.OpenReadStream();
        var result = await create.ExecuteAsync(workspaceId, userId, file.FileName, stream, ct);
        return Ok(new CsvUploadResponse(result.BatchId, result.Headers, result.SampleRows, result.RowCount, GuessMapping(result.Headers)));
    }
    catch (CsvFormatException ex) { return Problem(ex.Message, statusCode: 400); }
}

[HttpPost("{batchId:guid}/start")]
public async Task<IActionResult> Start(Guid batchId, ColumnMappingRequest request, CancellationToken ct) { /* validate → StartCsvImportUseCase → 202 / 404 / 409 / 400 with problems */ }

[HttpGet("{batchId:guid}/status")]  public async Task<ActionResult<ImportStatusResponse>> Status(Guid batchId, CancellationToken ct) { /* 404 for other tenant */ }

[HttpGet("{batchId:guid}/errors")]
public async Task<IActionResult> Errors(Guid batchId, CancellationToken ct)
    => File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"import-{batchId:N}-issues.csv");
```
`GuessMapping(headers)` — a server-side convenience returned with the preview so the UI starts pre-filled (F16 also guesses client-side for instant feedback, but the server version is the tested one): case-insensitive, punctuation-insensitive match of headers against synonym lists (`email`/`e-mail`/`email address`/`work email`; `first name`/`firstname`/`given name`; `last name`/…; `name`/`full name`; `title`/`job title`/`position`; `company`/`company name`/`organization`/`organisation`; `domain`/`website`/`company domain`/`url`; `industry`; `country`; `size`/`employees`). Put it in `Application/Csv/ColumnGuesser.cs` (pure, tested) so F16 can port the same table.
Rate limit: add `UploadPolicy` (5/min per workspace) to `RateLimiting.cs`. The status endpoint is polled every 2 s by the UI — leave it on the default (unlimited authenticated) policy.
Register in `Program.cs`: `IBlobStorage`, `ICsvRowReader`, `ICsvImportRepository`, the four use cases, `ProcessCsvImportJob`, `BlobServiceClient`. `appsettings.Development.json` gets `"Storage": { "ConnectionString": "UseDevelopmentStorage=true" }` (not a secret). README "Local setup": `npm i -g azurite` (or the VS Azurite component) and `azurite --silent --location %TEMP%\azurite`.

## Tests (test-first; PBI 15.8)
**`Infrastructure/Csv/CsvHelperRowReaderTests.cs`** (pure, in-memory streams — no DB, no Azurite)
- UTF-8 with BOM; UTF-8 without BOM with `é`/`ü`/CJK names round-trip intact; Latin-1 bytes (`0xE9`) decode to `é`; delimiter `;`, tab, `|` detected; quoted delimiter inside a field does not fool the sniffer.
- Quoted field with embedded newline counts as one record; trailing blank lines ignored; `\r\n` and `\n`.
- Malformed quote mid-file → that record reported with `Error`, the following valid records still read (or, if CsvHelper cannot recover, the documented fail-with-row-number behaviour).
- Empty file, header-only, blank header cell, duplicate header names, binary (PNG bytes), > `maxRows`, > 100 columns → `CsvFormatException` with specific messages.
- Missing trailing fields → empty strings, not exceptions; extra fields ignored.
- 10k-row sanity: preview + full read under ~2 s (generous bound; fails only on accidental O(n²)).

**`Application/Csv/CsvRowMapperTests.cs`**, **`ColumnGuesserTests.cs`** — mapping first+last → name; blank cells → null; mapping validation (missing email, header not in file, `Name` together with `FirstName`); every synonym row.

**`Application/Csv/ProcessCsvImportUseCaseTests.cs`** (fakes: `FakeCsvImportRepository`, `InMemoryBlobStorage`, in-memory reader over a string, `FakeLeadIngestionStore` from F11) — counts add up (`Imported + Duplicates + Suppressed + Invalid == RowCount`); batches of 500 commit separately (assert `UpdateAsync` call count for 1,200 rows = 3); **re-run of a completed batch is a no-op; re-running the same *file* as a new batch imports 0 and reports all as duplicates**; crash after batch 1 (fake store throws on second flush) then re-run resumes at `ProcessedRowCount` without inflating counts; missing blob → `Failed` with message; error log capped at 1,000 while `ErrorCount` stays true; **cross-workspace**: a batch id from workspace A processed with workspace B's id finds nothing; unsubscribed leads in the file → `Suppressed`.

**`Application/Csv/ErrorCsvWriterTests.cs`** — quoting of commas/quotes/newlines; `=cmd|' /C calc'!A0` and `@SUM(…)` cells are prefixed with `'`.

**`Api/LeadImportControllerTests.cs`** — non-`.csv` → 400; > 10 MB → 413; other tenant's `batchId` → 404 on `start`/`status`/`errors`; `start` twice → 409; mapping without email → 400 with problem list.

**`Infrastructure/Storage/AzureBlobStorageTests.cs`** — marked `[Trait("Category","Azurite")]`, skipped unless `AZURITE=1`; save/open/delete round trip and seekability. Not part of the default CI run.

## Not in this feature
The wizard UI (F16), re-mapping after start, importing into an existing campaign directly, Excel (`.xlsx`) files, background cleanup of old blobs (F30 lifecycle rule handles it), per-row "update existing lead" behaviour (duplicates are skipped, never merged).

## Verification
Start Azurite and the API. Using `ZenLead.Api.http` (multipart sample added there): upload a messy 5–10k row file (mixed case emails, blank rows, a `;`-delimited variant, a Latin-1 export, duplicate rows, an unsubscribed lead present in the DB) → preview returns headers/sample/guessed mapping; `start` → poll `status` until `Completed`; counts reconcile; open `/hangfire` to see the job; download `errors` and open in Excel (no formulas execute); upload the same file again → 0 imported; kill the API mid-import and restart → the job resumes and the final count equals the file's valid unique rows.
