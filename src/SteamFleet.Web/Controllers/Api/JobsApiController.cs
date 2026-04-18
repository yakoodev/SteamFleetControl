using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SteamFleet.Contracts.Enums;
using SteamFleet.Contracts.Jobs;
using SteamFleet.Persistence.Helpers;
using SteamFleet.Persistence.Services;

namespace SteamFleet.Web.Controllers.Api;

[ApiController]
[IgnoreAntiforgeryToken]
[Authorize]
[Route("api/jobs")]
public sealed class JobsApiController(IJobService jobService, IBackgroundJobClient backgroundJobs) : ControllerBase
{
    private string ActorId => User.Identity?.Name ?? "system";
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost]
    public async Task<ActionResult<JobDto>> Create([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
    {
        var job = await jobService.CreateAsync(request, ActorId, ClientIp, cancellationToken);
        backgroundJobs.Enqueue<HangfireJobExecutor>(x => x.ExecuteAsync(job.Id, CancellationToken.None));
        return CreatedAtAction(nameof(GetById), new { id = job.Id }, job);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<JobDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobService.GetByIdAsync(id, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet("{id:guid}/items")]
    public async Task<ActionResult<IReadOnlyCollection<JobItemDto>>> GetItems(Guid id, CancellationToken cancellationToken)
    {
        var items = await jobService.GetItemsAsync(id, cancellationToken);
        return Ok(items);
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var canceled = await jobService.CancelAsync(id, ActorId, ClientIp, cancellationToken);
        return canceled ? NoContent() : NotFound();
    }

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("profile-update")]
    public Task<ActionResult<JobDto>> ProfileUpdate([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.ProfileUpdate, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("privacy-update")]
    public Task<ActionResult<JobDto>> PrivacyUpdate([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.PrivacyUpdate, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("avatar-update")]
    public Task<ActionResult<JobDto>> AvatarUpdate([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.AvatarUpdate, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("tags-assign")]
    public Task<ActionResult<JobDto>> TagsAssign([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.TagsAssign, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("group-move")]
    public Task<ActionResult<JobDto>> GroupMove([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.GroupMove, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("session-validate")]
    public Task<ActionResult<JobDto>> SessionValidate([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.SessionValidate, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("session-refresh")]
    public Task<ActionResult<JobDto>> SessionRefresh([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.SessionRefresh, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("add-note")]
    public Task<ActionResult<JobDto>> AddNote([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.AddNote, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("password-change")]
    public Task<ActionResult<JobDto>> PasswordChange([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.PasswordChange, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("sessions-deauthorize")]
    public Task<ActionResult<JobDto>> SessionsDeauthorize([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.SessionsDeauthorize, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("friends-add-by-invite")]
    public Task<ActionResult<JobDto>> FriendsAddByInvite([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.FriendsAddByInvite, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin + "," + Roles.Operator)]
    [EnableRateLimiting("sensitive")]
    [HttpPost("friends-connect-family-main")]
    public Task<ActionResult<JobDto>> FriendsConnectFamilyMain([FromBody] JobCreateRequest request, CancellationToken cancellationToken)
        => CreateJobAsync(JobType.FriendsConnectFamilyMain, request, cancellationToken);

    [Authorize(Roles = Roles.SuperAdmin + "," + Roles.Admin)]
    [EnableRateLimiting("sensitive")]
    [HttpGet("{id:guid}/sensitive-report")]
    public async Task<IActionResult> SensitiveReport(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var csv = await jobService.DownloadSensitiveReportOnceAsync(id, ActorId, ClientIp, cancellationToken);
            if (csv is null)
            {
                return NotFound();
            }

            return File(csv, "text/csv", $"job-sensitive-report-{id}.csv");
        }
        catch (InvalidOperationException)
        {
            return Forbid();
        }
    }

    private async Task<ActionResult<JobDto>> CreateJobAsync(JobType type, JobCreateRequest request, CancellationToken cancellationToken)
    {
        request.Type = type;
        var job = await jobService.CreateAsync(request, ActorId, ClientIp, cancellationToken);
        backgroundJobs.Enqueue<HangfireJobExecutor>(x => x.ExecuteAsync(job.Id, CancellationToken.None));
        return CreatedAtAction(nameof(GetById), new { id = job.Id }, job);
    }
}
