using Bloxstrap.Services.Data;
using Bloxstrap.Services.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;

namespace Bloxstrap.Services.Controllers
{
    [Route("[controller]")]
    [ApiController]
    [EnableRateLimiting("metrics")]
    public partial class MetricsController(IInfluxDBClient influxDBClient, IMemoryCache memoryCache,
        IHttpClientFactory httpClientFactory, ApplicationDbContext dbContext,
        ILogger<MetricsController> logger, IWebHostEnvironment env) : ControllerBase
    {
        private readonly List<StatPoint> _statPoints = 
        [
            new StatPoint
            {
                Name = "installAction",
                Bucket = "bloxstrap-90d",
                Values = ["install", "upgrade", "uninstall"]
            },

            new StatPoint
            {
                Name = "robloxChannel",
                Bucket = "bloxstrap-14d",
                BucketPublic = "bloxstrap-14d-public"
            }
        ];

        private async Task<bool> IsHttpRequestSuccessCached(TimeSpan timeSpan, string key, string url)
        {
            if (!memoryCache.TryGetValue(key, out bool result))
            {
                var httpClient = httpClientFactory.CreateClient();
                var response = await httpClient.GetAsync(url);

                result = response.IsSuccessStatusCode;
                memoryCache.Set(key, result, timeSpan);
            }

            return result;
        }

        [HttpGet("post")]
        public async Task<IActionResult> Post(string? key, string? value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                return BadRequest();

            var statPoint = _statPoints.Find(x => x.Name == key);

            if (statPoint is null || statPoint.Values is not null && !statPoint.Values.Contains(value))
                return BadRequest();

            List<string> buckets = [statPoint.Bucket];

            if (statPoint.Name == "robloxChannel")
            {
                value = value.ToLowerInvariant();

                if (value[0] != 'z')
                    return BadRequest();

                bool valid = await IsHttpRequestSuccessCached(
                    TimeSpan.FromDays(1), 
                    $"channel-{value}-valid", 
                    $"https://clientsettings.roblox.com/v2/settings/application/PCClientBootstrapper/bucket/{value}"
                );

                if (!valid)
                {
                    logger.LogWarning("Requested nonexistent channel ({Channel})", value);
                    return BadRequest();
                }

                bool publicChannel = await IsHttpRequestSuccessCached(
                    TimeSpan.FromMinutes(30),
                    $"channel-{value}-public", 
                    $"https://clientsettings.roblox.com/v2/client-version/WindowsPlayer/channel/{value}"
                );

                if (publicChannel)
                    buckets.Add(statPoint.BucketPublic);
            }

            // TODO: batching?
            // batching data here could cause potential loss of data, maybe we could do
            // it in a cron job retroactively instead?
            var point = PointData.Measurement(key)
                    .Field(value, 1)
                    .Tag("version", HttpContext.Items["ClientVersion"]!.ToString())
                    .Timestamp(DateTime.UtcNow, WritePrecision.Ms);

            if (env.IsDevelopment())
                buckets = ["test-bucket"];

            foreach (string bucket in buckets)
                await influxDBClient.GetWriteApiAsync().WritePointAsync(point, bucket);

            return Ok();
        }

        [HttpPost("post-exception")]
        public async Task<IActionResult> PostException()
        {
            // TODO: matt's v2 code
            // TODO: client website
            using var sr = new StreamReader(Request.Body);
            string trace = await sr.ReadToEndAsync();

            if (string.IsNullOrEmpty(trace))
                return BadRequest();

            if (trace.Length >= 1024*50)
            {
                logger.LogWarning("Exception post dropped because it was too big ({Bytes} bytes)", trace.Length);
                return StatusCode(413);
            }

            await dbContext.ExceptionReports.AddAsync(new()
            {
                Timestamp = DateTime.UtcNow,
                Trace = trace
            });

            await dbContext.SaveChangesAsync();

            return Ok();
        }
    }
}
