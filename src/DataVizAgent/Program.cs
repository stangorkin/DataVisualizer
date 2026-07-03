using DataVizAgent.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

// ── Services ────────────────────────────────────────────────────────────────
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddDataVizAgentCore(builder.Configuration);

// ── App pipeline ─────────────────────────────────────────────────────────────
WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<DataVizAgent.Components.App>()
    .AddAdditionalAssemblies(typeof(DataVizAgent.Components.Pages.Home).Assembly)
   .AddInteractiveServerRenderMode();

await app.RunAsync();
