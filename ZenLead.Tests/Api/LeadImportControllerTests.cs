using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZenLead.Api.Controllers.V1;
using ZenLead.Application.Dtos.Leads;
using ZenLead.Domain.Enums;
using ZenLead.Tests.Application.Csv;

namespace ZenLead.Tests.Api;

public class LeadImportControllerTests
{
    private readonly CsvImportHarness _h = new();

    private LeadImportController Controller(Guid? workspaceId = null)
    {
        var controller = new LeadImportController(_h.Create, _h.Start, _h.Batches);
        var identity = new ClaimsIdentity([new Claim("workspace_id", (workspaceId ?? _h.Workspace).ToString()), new Claim("sub", _h.User.ToString())], "test");
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return controller;
    }

    private static FormFile File(string content, string name = "leads.csv", long? declaredLength = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, declaredLength ?? bytes.Length, "file", name);
    }

    private static ColumnMappingRequest Mapping(string? email = "Email")
        => new(null, "First", "Last", email, "Title", "Company", null, null, null, null);

    private static int? StatusOf(IActionResult? result) => (result as ObjectResult)?.StatusCode ?? (result as StatusCodeResult)?.StatusCode;

    private async Task<Guid> UploadOk(LeadImportController c)
    {
        var result = await c.Upload(File(CsvImportHarness.Csv(5)), CancellationToken.None);
        return Assert.IsType<CsvUploadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value).BatchId;
    }

    [Fact]
    public async Task Upload_ReturnsPreview_AndAGuessedMapping()
    {
        var result = await Controller().Upload(File(CsvImportHarness.Csv(5)), CancellationToken.None);

        var body = Assert.IsType<CsvUploadResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(5, body.RowCount);
        Assert.Equal("Email", body.SuggestedMapping.Email);
        Assert.Equal("Title", body.SuggestedMapping.Title);
    }

    [Fact]
    public async Task Upload_NonCsvExtension_Is400()
    {
        var result = await Controller().Upload(File("a,b\n1,2\n", name: "leads.xlsx"), CancellationToken.None);

        Assert.Equal(400, StatusOf(result.Result));
        Assert.Empty(_h.Blobs.Blobs);
    }

    [Fact]
    public async Task Upload_NoFile_Is400()
    {
        Assert.Equal(400, StatusOf((await Controller().Upload(null, CancellationToken.None)).Result));
        Assert.Equal(400, StatusOf((await Controller().Upload(File(""), CancellationToken.None)).Result));
    }

    [Fact]
    public async Task Upload_LargerThan10Mb_Is413()
    {
        var result = await Controller().Upload(File("Email\na@x.com\n", declaredLength: 10 * 1024 * 1024 + 1), CancellationToken.None);

        Assert.Equal(413, StatusOf(result.Result));
        Assert.Empty(_h.Blobs.Blobs);
    }

    [Fact]
    public async Task Upload_OfABadCsv_Is400WithTheReason()
    {
        var result = await Controller().Upload(File("Email,Name\n"), CancellationToken.None);

        var problem = Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result.Result).Value);
        Assert.Equal(400, problem.Status);
        Assert.Contains("no data rows", problem.Detail);
    }

    [Fact]
    public async Task Start_HappyPath_Is202_AndStatusReflectsTheBatch()
    {
        var c = Controller();
        var id = await UploadOk(c);

        Assert.IsType<AcceptedResult>(await c.Start(id, Mapping(), CancellationToken.None));
        var status = Assert.IsType<ImportStatusResponse>(Assert.IsType<OkObjectResult>((await c.Status(id, CancellationToken.None)).Result).Value);

        Assert.Equal(CsvImportStatus.Parsing, status.Status);
        Assert.Equal(5, status.RowCount);
        Assert.Equal(0, status.Percent);
    }

    [Fact]
    public async Task Start_Twice_Is409()
    {
        var c = Controller();
        var id = await UploadOk(c);
        await c.Start(id, Mapping(), CancellationToken.None);

        Assert.Equal(409, StatusOf(await c.Start(id, Mapping(), CancellationToken.None)));
    }

    [Fact]
    public async Task Start_WithoutAnEmailMapping_Is400WithTheProblemList()
    {
        var c = Controller();
        var id = await UploadOk(c);

        var result = await c.Start(id, Mapping(email: null), CancellationToken.None);

        var problem = Assert.IsAssignableFrom<ValidationProblemDetails>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Contains(problem.Errors["mapping"], m => m.Contains("email", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(400, StatusOf(result));
        Assert.Empty(_h.Scheduler.CsvImports);
    }

    [Fact]
    public async Task AnotherTenantsBatch_Is404_OnStartStatusAndErrors()
    {
        var id = await UploadOk(Controller());
        var intruder = Controller(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(await intruder.Start(id, Mapping(), CancellationToken.None));
        Assert.IsType<NotFoundResult>((await intruder.Status(id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>(await intruder.Errors(id, CancellationToken.None));
    }

    [Fact]
    public async Task Errors_ReturnsACsvReport_OfTheLoggedIssues()
    {
        var c = Controller();
        var upload = await c.Upload(File("Email,First,Last,Title,Company\nnope,A,B,CTO,Acme\nok@acme.com,A,B,CTO,Acme\n"), CancellationToken.None);
        var id = Assert.IsType<CsvUploadResponse>(Assert.IsType<OkObjectResult>(upload.Result).Value).BatchId;
        await c.Start(id, Mapping(), CancellationToken.None);
        await _h.Process.ExecuteAsync(id, _h.Workspace, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(await c.Errors(id, CancellationToken.None));
        var text = Encoding.UTF8.GetString(file.FileContents);

        Assert.StartsWith("text/csv", file.ContentType);
        Assert.StartsWith("row,outcome,reason,email,name", text);
        Assert.Contains("1,Invalid,Invalid email address,nope", text);
        Assert.DoesNotContain("ok@acme.com", text);
    }

    [Fact]
    public async Task Status_AfterCompletion_Reports100Percent()
    {
        var c = Controller();
        var id = await UploadOk(c);
        await c.Start(id, Mapping(), CancellationToken.None);
        await _h.Process.ExecuteAsync(id, _h.Workspace, CancellationToken.None);

        var status = Assert.IsType<ImportStatusResponse>(Assert.IsType<OkObjectResult>((await c.Status(id, CancellationToken.None)).Result).Value);

        Assert.Equal(CsvImportStatus.Completed, status.Status);
        Assert.Equal(100, status.Percent);
        Assert.Equal(5, status.ImportedCount);
    }
}
