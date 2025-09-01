using Bloxstrap.Services.Data;
using Bloxstrap.Services.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;

using System.Text.Json;

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
                Bucket = "bloxstrap-30d",
                Values = ["install", "upgrade", "uninstall"]
            },

            new StatPoint
            {
                Name = "robloxChannel",
                Bucket = "bloxstrap-14d"
            }
        ];

        [HttpGet("post")]
        public async Task<IActionResult> Post(string? key, string? value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                return BadRequest();

            var statPoint = _statPoints.Find(x => x.Name == key);

            if (statPoint is null || statPoint.Values is not null && !statPoint.Values.Contains(value))
                return BadRequest();

            if (statPoint.Name == "robloxChannel")
            {
                value = value.ToLowerInvariant();

                if (value[0] != 'z')
                    return BadRequest();

                string validationCacheKey = $"validation-channel-{value}";

                if (!memoryCache.TryGetValue(validationCacheKey, out bool exists))
                {
                    var httpClient = httpClientFactory.CreateClient();

                    // TODO: retries on errored requests
                    var response = await httpClient.GetFromJsonAsync<JsonDocument>($"https://clientsettings.roblox.com/v2/settings/application/PCClientBootstrapper/bucket/{value}");
                    
                    exists = response!.RootElement.GetProperty("applicationSettings").ValueKind != JsonValueKind.Null;

                    memoryCache.Set(validationCacheKey, exists, DateTime.Now.AddDays(1));
                }

                if (!exists)
                {
                    logger.LogWarning("Requested nonexistent channel ({Channel})", value);
                    return BadRequest();
                }
            }

            string influxBucket = statPoint.Bucket;

            if (env.IsDevelopment())
                influxBucket = "test-bucket";

            // TODO: batching? (won't know if necessary yet until channel analytics fill up)
            var point = PointData.Measurement(key)
                    .Field(value, 1)
                    .Tag("version", HttpContext.Items["ClientVersion"]!.ToString())
                    .Timestamp(DateTime.UtcNow, WritePrecision.S);

            await influxDBClient.GetWriteApiAsync().WritePointAsync(point, influxBucket);

            return Ok();
        }

        [HttpPost("post-exception")]
        public async Task<IActionResult> PostException()
        {
            // TODO: matt's v2 code
            using var sr = new StreamReader(Request.Body);
            string trace = await sr.ReadToEndAsync();

            if (string.IsNullOrEmpty(trace))
                return BadRequest();

            if (trace.Length >= 1024*50)
            {
                logger.LogWarning("Exception post dropped because it was too big ({Bytes} bytes)", trace.Length);
                return BadRequest();
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
