using System.Net;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

using Bloxstrap.Services.Controllers;
using Bloxstrap.Services.Data;

using InfluxDB.Client;

using Polly;
using Polly.Extensions.Http;
using Polly.Retry;

namespace Bloxstrap.Services
{
    public partial class Program
    {
        static AsyncRetryPolicy<HttpResponseMessage> GetHttpPolicy()
        {
            return HttpPolicyExtensions
                .HandleTransientHttpError()
                .WaitAndRetryAsync(5, _ => TimeSpan.FromSeconds(5));
        }

        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            builder.Services.AddDbContext<ApplicationDbContext>();
            builder.Services.AddMemoryCache();
            builder.Services.AddHttpClient<MetricsController>().AddPolicyHandler(GetHttpPolicy());

            // todo: influxdb health checks
            // the health check framework isn't suitable for us and needs to be forked
            builder.Services.AddHealthChecks()
                .AddNpgSql(builder.Configuration.GetConnectionString("Postgres")!);

            builder.Services.AddSingleton<IInfluxDBClient, InfluxDBClient>(x =>
            {
                var options = new InfluxDBClientOptions(builder.Configuration["InfluxDB:Address"])
                {
                    Token = builder.Configuration["InfluxDB:Token"],
                    Org = builder.Configuration["InfluxDB:Organisation"]
                };

                return new InfluxDBClient(options);
            });

            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                string? forwardHeader = builder.Configuration["ProxyConfig:ForwardedForHeader"];
                string? proxyAddress = builder.Configuration["ProxyConfig:ProxyAddress"];

                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;

                if (forwardHeader is not null)
                    options.ForwardedForHeaderName = forwardHeader;

                if (proxyAddress is not null)
                    options.KnownProxies.Add(IPAddress.Parse(proxyAddress));
            });

            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.AddPolicy("metrics", context => RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromHours(1)
                    }
                ));
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline.

            //app.UseAuthorization();

            app.UseForwardedHeaders();
            app.UseRateLimiter();

            app.MapControllers().AddEndpointFilter(async (efiContext, next) =>
            {
                efiContext.HttpContext.Request.Headers.TryGetValue("User-Agent", out var uaHeader);

                if (uaHeader.Count != 0)
                {
                    string ua = uaHeader[0]!;
                    var match = UARegex().Match(ua);

                    if (match.Success)
                    {
                        string info = match.Groups[2].Value;

                        if (info == "Production")
                        {
                            efiContext.HttpContext.Items["ClientVersion"] = ua;
                            return await next(efiContext);
                        }
                    }
                }

                return Results.BadRequest();
            });

            app.MapGet("/", () => Results.Redirect("https://bloxstraplabs.com"));
            app.MapHealthChecks("/health");

            if (app.Environment.IsDevelopment())
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.Database.Migrate();
            }

            app.Run();
        }

        [GeneratedRegex(@"^Bloxstrap\/([0-9\.]{5,6}) \((Production|Build [a-zA-Z0-9=+\/]+|Artifact [0-9a-f]{40}, [a-zA-Z0-9\/\-]+)\)$")]
        private static partial Regex UARegex();
    }
}
