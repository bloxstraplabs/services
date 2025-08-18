using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

using Bloxstrap.Services.Data;

using InfluxDB.Client;

namespace Bloxstrap.Services
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddControllers();
            builder.Services.AddHealthChecks();
            builder.Services.AddDbContext<ApplicationDbContext>();
            builder.Services.AddMemoryCache();
            builder.Services.AddHttpClient();

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
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor
                    | ForwardedHeaders.XForwardedProto
                    | ForwardedHeaders.XForwardedHost;
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

                    var match = Regex.Match(ua, @"Bloxstrap\/([0-9\.]+) \((Production|Build [a-zA-Z0-9=+\/]+|Artifact [0-9a-f]{40}, [a-zA-Z0-9\/\-]+)\)");

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

            app.Map("/", () => { return Results.Redirect("https://bloxstraplabs.com"); });
            app.MapHealthChecks("/health");

            if (app.Environment.IsDevelopment())
            {
                using (var scope = app.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    db.Database.Migrate();
                }
            }

            app.Run();
        }
    }
}
