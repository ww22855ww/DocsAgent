using Microsoft.AspNetCore.Mvc;
using MockPortal.Infrastructure;
using MockPortal.Models;

namespace MockPortal.Controllers;

[RequireLogin]
public class DocumentsController : Controller
{
    private readonly MockDocumentStore _store;
    private readonly ILogger<DocumentsController> _log;

    public DocumentsController(MockDocumentStore store, ILogger<DocumentsController> log)
    {
        _store = store;
        _log = log;
    }

    [HttpGet]
    public IActionResult Query()
        => View(new QueryViewModel { QueryDate = DateTime.Now.ToString("yyyy-MM-dd") });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Query(QueryViewModel model)
    {
        model.Searched = true;
        model.Results = _store.QueryDocuments(model.Department, model.DocumentType);
        _log.LogInformation("SCM query dept={Dept} type={Type} date={Date} -> {Count} rows",
            model.Department, model.DocumentType, model.QueryDate, model.Results.Count);
        return View(model);
    }

    [HttpGet]
    public IActionResult Download(string file)
    {
        var path = _store.ResolvePath(PortalSection.ScmDocuments, file);
        if (path is null)
        {
            _log.LogWarning("SCM download rejected for {File}", file);
            return NotFound();
        }

        _log.LogInformation("SCM download {File}", file);
        var name = Path.GetFileName(path);
        return PhysicalFile(path, MockDocumentStore.ContentTypeFor(name), name);
    }
}
