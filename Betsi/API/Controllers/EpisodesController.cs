namespace Betsi.API.Controllers;

using Betsi.API.Problems;
using Betsi.Application.Queries;
using Betsi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>Episode detail and the waiting board (MVP-062). Every read is audited.</summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public sealed class EpisodesController : ControllerBase
{
    private readonly EpisodeQueries _queries;

    public EpisodesController(EpisodeQueries queries)
    {
        _queries = queries;
    }

    /// <summary>One episode: demographics, state, timings and its escalations.</summary>
    [HttpGet("episodes/{episodeId:guid}")]
    [Authorize(Policy = Permissions.EpisodesRead)]
    [AuditRead("Episode", "episodeId")]
    [ProducesResponseType<EpisodeDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEpisode(Guid episodeId, CancellationToken cancellationToken) =>
        await _queries.GetEpisodeAsync(episodeId, cancellationToken) is { } episode
            ? Ok(episode)
            : NotFoundProblem($"Episode '{episodeId}' was not found.");

    /// <summary>Patients waiting or awaiting treatment, longest wait first, a page at a time.</summary>
    /// <param name="locationId">Only patients at this location.</param>
    /// <param name="state"><c>Waiting</c> or <c>AwaitingTreatment</c>. Both if omitted.</param>
    /// <param name="minWaitingMinutes">Only patients who have waited at least this long.</param>
    /// <param name="minAgeYears">Only patients at least this old.</param>
    /// <param name="maxAgeYears">Only patients at most this old, e.g. 17 for paediatrics.</param>
    /// <param name="cursor">The <c>nextCursor</c> from the previous page.</param>
    /// <param name="pageSize">1–200, default 50.</param>
    [HttpGet("boards/waiting")]
    [Authorize(Policy = Permissions.EpisodesRead)]
    [AuditRead("WaitingBoard")]
    [ProducesResponseType<WaitingBoardPage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetWaitingBoard(
        [FromQuery] Guid? locationId,
        [FromQuery] string? state,
        [FromQuery] int? minWaitingMinutes,
        [FromQuery] int? minAgeYears,
        [FromQuery] int? maxAgeYears,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = WaitingBoardFilter.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _queries.GetWaitingBoardAsync(
                new WaitingBoardFilter(locationId, state, minWaitingMinutes, minAgeYears, maxAgeYears, cursor, pageSize),
                cancellationToken));
        }
        catch (InvalidQueryException exception)
        {
            var problem = ProblemCodes.Create(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "Invalid query", exception.Message);
            return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
        }
    }

    private ObjectResult NotFoundProblem(string detail)
    {
        var problem = ProblemCodes.Create(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "Resource not found", detail);
        return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { "application/problem+json" } };
    }
}
