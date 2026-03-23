using Autofac.Extensions.DependencyInjection;
using Nop.Core.Configuration;
using Nop.Core.Infrastructure;
using Nop.Services.Telemetry;
using Nop.Web.Framework.Infrastructure.Extensions;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace Nop.Web;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Configuration.AddJsonFile(NopConfigurationDefaults.AppSettingsFilePath, true, true);
        if (!string.IsNullOrEmpty(builder.Environment?.EnvironmentName))
        {
            var path = string.Format(NopConfigurationDefaults.AppSettingsEnvironmentFilePath, builder.Environment.EnvironmentName);
            builder.Configuration.AddJsonFile(path, true, true);
        }
        builder.Configuration.AddEnvironmentVariables();

        //load application settings
        builder.Services.ConfigureApplicationSettings(builder);

        var appSettings = Singleton<AppSettings>.Instance;
        var useAutofac = appSettings.Get<CommonConfig>().UseAutofac;

        if (useAutofac)
            builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
        else
        {
            builder.Host.UseDefaultServiceProvider(options =>
            {
                //we don't validate the scopes, since at the app start and the initial configuration we need 
                //to resolve some services (registered as "scoped") through the root container
                options.ValidateScopes = false;
                options.ValidateOnBuild = true;
            });
        }

        //add services to the application and configure service provider
        builder.Services.ConfigureApplicationServices(builder);

        //configure OpenTelemetry tracing and metrics (OTLP → OTel Collector → Jaeger / Prometheus)
        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(NopTelemetry.ServiceName, serviceVersion: NopTelemetry.Version))
            .WithTracing(tracing => tracing
                .AddSource(NopTelemetry.CatalogSource.Name)
                .AddSource("Npgsql")
                .AddAspNetCoreInstrumentation(opts => opts.RecordException = true)
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(opts => opts.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .AddMeter(NopTelemetry.CatalogMeterName)
                .AddAspNetCoreInstrumentation()
                .AddOtlpExporter(opts => opts.Endpoint = new Uri(otlpEndpoint)));

        var app = builder.Build();

        //configure the application HTTP request pipeline
        app.ConfigureRequestPipeline();
        await app.PublishAppStartedEventAsync();

        await app.RunAsync();
    }
}