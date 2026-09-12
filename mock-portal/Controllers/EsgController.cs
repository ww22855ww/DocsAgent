using Microsoft.AspNetCore.Mvc;
using MockPortal.Infrastructure;
using MockPortal.Models;

namespace MockPortal.Controllers;

/// <summary>
/// The ESG questionnaire section. A separate screen with its own filters and its
/// own download route, so reaching these files is a genuinely different path
/// through the portal than reaching the SCM documents.
/// </summary>
[RequireLogin]
public class EsgController : Controller
{
    private readonly MockDocumentStore _store;
    private readonly ILogger<EsgController> _log;

    public EsgController(MockDocumentStore store, ILogger<EsgController> log)
    {
        _store = store;
        _log = log;
    }

    [HttpGet]
    public IActionResult Surveys() => View(new EsgQueryViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Surveys(EsgQueryViewModel model)
    {
        model.Searched = true;
        model.Results = _store.QuerySurveys(model.Status);
        _log.LogInformation("ESG query year={Year} status={Status} -> {Count} rows",
            model.Year, model.Status, model.Results.Count);
        return View(model);
    }

    [HttpGet]
    public IActionResult Download(string file)
    {
        var path = _store.ResolvePath(PortalSection.EsgSurveys, file);
        if (path is null)
        {
            _log.LogWarning("ESG download rejected for {File}", file);
            return NotFound();
        }

        _log.LogInformation("ESG download {File}", file);
        var name = Path.GetFileName(path);
        return PhysicalFile(path, MockDocumentStore.ContentTypeFor(name), name);
    }
}
