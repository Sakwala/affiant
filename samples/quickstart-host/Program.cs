using Affiant.Abstractions.Interfaces;
using Affiant.Abstractions.Models;
using Affiant.Core.Extensions;
using Affiant.Docket.Extensions;
using Affiant.EntityFramework;
using Affiant.EntityFramework.Extensions;
using Affiant.EntityFramework.Migrations;
using Affiant.SemanticKernel.Extensions;
using Affiant.Transport.SignalR.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using QuickstartHost.Agent;
using QuickstartHost.Data;
using QuickstartHost.DevSeam;
using QuickstartHost.Execution;
using QuickstartHost.Hubs;
using QuickstartHost.Projection;
using QuickstartHost.Review;

var builder = WebApplication.CreateBuilder(args);

// ─────────────────────────────────────────────────────────────────────────────
// 1. Register the framework
//
// AddAffiantCore wires the tool registry, the context fabric, the policy evaluator, the review
// gate and the deterministic pre-tool filters. AddAffiantSemanticKernel adds the Semantic Kernel
// adapter: the startup validator, and the two post-tool filters that intercept a write tool's
// result — one merges inference, the other files the review.
//
// Neither registers an approval policy. With none registered, the evaluator's own fallback asks
// for reviewer confirmation, which is the always-ask-a-human default this sample wants. Add
// Affiant.Policies only when some writes should be pre-authorised or escalated.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();

builder.Services.AddAffiantCore();
builder.Services.AddAffiantSemanticKernel();

// ─────────────────────────────────────────────────────────────────────────────
// 2. Persistence and the review transport
//
// The order of the next two calls matters. AddAffiantEntityFramework(UseSqlite) gives you
// AffiantDbContext, the chat-session store, and a SQLite-backed docket store.
// AddAffiantDocket(UseInMemory) then registers a second docket store, and because it is
// registered last it is the one that resolves. That is deliberate here: an in-memory docket hands
// an approved affidavit back as the same objects the proposal built, rather than values that have
// round-tripped through JSON. AddAffiantDocket is also required either way — it is what registers
// the expiry sweep that moves an unreviewed entry to Expired.
//
// A host that wants review state to survive a restart drops the AddAffiantDocket(UseInMemory)
// argument and lets the SQLite store stand, at the cost of reading each field back as JSON.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddAffiantEntityFramework(o => o.UseSqlite("Data Source=affiant-quickstart.db"));
builder.Services.AddAffiantDocket(o => o.UseInMemory());
builder.Services.AddAffiantSignalR<ChatHub>();

// The sample's own domain database — separate from Affiant's.
builder.Services.AddDbContext<HrDbContext>(o => o.UseSqlite("Data Source=hr-quickstart.db"));

// ─────────────────────────────────────────────────────────────────────────────
// 3–5. The write domain: one field schema, two write tools, one read tool
//
// AddAffiantTool registers the strategy in DI and a matching descriptor in the registry, in one
// step. Pass the plugin name: Semantic Kernel reports a function under its plugin, and the startup
// validator looks descriptors up by both, so a descriptor registered without one is not found and
// the host refuses to start.
//
// AddAffidavitProjection is what makes an update-shaped write carry an entity id and each field's
// previous value. Without it the framework's default projection applies and both are null — fine
// for a create, wrong for an update. See LeaveAffidavitProjection.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddAffiantTool<LeaveTaskInferenceStrategy>(
    functionName: RequestLeavePlugin.FunctionName,
    operation: Operation.WriteCreate,
    entityType: LeaveTaskInferenceStrategy.LeaveRequestEntity,
    pluginName: nameof(RequestLeavePlugin));

builder.Services.AddAffiantTool<LeaveTaskInferenceStrategy>(
    functionName: AmendLeavePlugin.FunctionName,
    operation: Operation.WriteUpdate,
    entityType: LeaveTaskInferenceStrategy.LeaveRequestEntity,
    pluginName: nameof(AmendLeavePlugin));

builder.Services.AddAffiantReadTool(
    functionName: LeaveLookupPlugin.FunctionName,
    entityType: LeaveTaskInferenceStrategy.LeaveRequestEntity,
    pluginName: nameof(LeaveLookupPlugin));

builder.Services.AddAffidavitProjection<LeaveAffidavitProjection>();

// The two host ports the framework refuses to start without once a write-capable tool is declared:
// what the record holds today (AF-3), and who may decide (AZ-2). Both are questions only the host
// can answer, and the startup refusal is what stops either being discovered mid-conversation.
builder.Services.AddPreviousValueSource<HrPreviousValueSource>();
builder.Services.AddDecisionAuthorization<QuickstartDecisionAuthorization>();
builder.Services.AddSingleton<LeaveProposalBuilder>();

// Registering plugin types with the kernel is ordinary Semantic Kernel, not Affiant. The chat
// completion connector below is added on the same builder and is outside Affiant's scope.
var kernelBuilder = builder.Services.AddKernel();
kernelBuilder.Plugins.AddFromType<RequestLeavePlugin>();
kernelBuilder.Plugins.AddFromType<AmendLeavePlugin>();
kernelBuilder.Plugins.AddFromType<LeaveLookupPlugin>();

// The model is optional. With no key the chat path says so and everything else still works —
// including the whole review lifecycle, through the development seam.
var openAiKey = builder.Configuration["OPENAI_API_KEY"];
var openAiModel = builder.Configuration["OPENAI_MODEL"] ?? "gpt-4o-mini";
var openAiBaseUrl = builder.Configuration["OPENAI_BASE_URL"];
if (!string.IsNullOrWhiteSpace(openAiKey))
{
    if (!string.IsNullOrWhiteSpace(openAiBaseUrl))
        kernelBuilder.AddOpenAIChatCompletion(openAiModel, new Uri(openAiBaseUrl), openAiKey);
    else
        kernelBuilder.AddOpenAIChatCompletion(openAiModel, openAiKey);
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. Review context
//
// The framework's review filter asks this for the session, tenant, user and reviewer behind a
// proposal, and files nothing if it gets null back. Registered scoped, because it reads the
// per-turn identity the hub sets.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<ChatTurnContext>();
builder.Services.AddScoped<IReviewContextProvider, HttpReviewContextProvider>();

// Rule 6's host half — UI guidance is a registration, not a DOM inspection: the framework's
// guidance bridge asks the UI layer which elements exist, by semantic id. It is a singleton, and
// ASP.NET Core validates singletons at build time in Development — so a host with no
// IRouteRegistry does not start there. See LeaveRouteRegistry. The numbered rules this sample's
// comments cite are defined in docs/affiant-framework-specification.md §6.
builder.Services.AddSingleton<IRouteRegistry, LeaveRouteRegistry>();

// ─────────────────────────────────────────────────────────────────────────────
// 8. The write port
//
// The only code in this sample that writes a leave request. Called from ChatHub.ApproveEntry
// after the framework confirms the entry actually reached Approved.
// ─────────────────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IWriteExecutor, LeaveWriteExecutor>();

// ─────────────────────────────────────────────────────────────────────────────
// 9. Build and run
// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var affiantDb = scope.ServiceProvider.GetRequiredService<AffiantDbContext>();
    await affiantDb.MigrateAffiantSchemaAsync(app.Logger);

    var hrDb = scope.ServiceProvider.GetRequiredService<HrDbContext>();
    await HrDbContext.SeedAsync(hrDb);
}

app.UseDefaultFiles();
app.UseStaticFiles();

// The employee list the reviewer's picker reads — a live read endpoint, so the value a reviewer
// puts on the card comes from the system of record rather than from typing.
app.MapGet("/api/employees", async (HrDbContext db, CancellationToken ct) =>
    await db.Employees.AsNoTracking().OrderBy(e => e.Name)
        .Select(e => new { e.Id, e.Name, e.Department })
        .ToListAsync(ct));

// The leave requests actually written. A reviewer's decision is only believable if you can see
// what it did, so the page and the regression deck both read this.
app.MapGet("/api/leave-requests", async (HrDbContext db, string? search, CancellationToken ct) =>
{
    var query = db.LeaveRequests.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
        query = query.Where(r => r.Reason.Contains(search) || r.Employee.Contains(search));

    return await query.OrderByDescending(r => r.Id)
        .Select(r => new
        {
            r.Id,
            r.Employee,
            StartDate = r.StartDate.ToString("yyyy-MM-dd"),
            EndDate = r.EndDate.ToString("yyyy-MM-dd"),
            r.LeaveType,
            r.Days,
            r.Reason,
            r.Status,
        })
        .ToListAsync(ct);
});

app.MapDevSeamEndpoints();
app.MapAffiantSignalR<ChatHub>();

app.Run();

/// <summary>
/// Named so the sample's tests can spin this host up in memory with
/// <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
