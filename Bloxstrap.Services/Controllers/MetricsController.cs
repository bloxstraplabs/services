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
    public partial class MetricsController : ControllerBase
    {
        private readonly IInfluxDBClient _influxDBClient;
        private readonly IMemoryCache _memoryCache;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ApplicationDbContext _dbContext;
        private readonly ILogger<MetricsController> _logger;
        private readonly IWebHostEnvironment _env;

        private readonly List<StatPoint> _statPoints = 
        [
            new StatPoint
            {
                Name = "installAction",
                Bucket = "bloxstrap",
                Values = ["install", "upgrade", "uninstall"]
            },

            new StatPoint
            {
                Name = "robloxChannel",
                Bucket = "bloxstrap-eph-7d"
            }
        ];

        public MetricsController(IInfluxDBClient influxDBClient, IMemoryCache memoryCache, 
            IHttpClientFactory httpClientFactory, ApplicationDbContext dbContext,
            ILogger<MetricsController> logger, IWebHostEnvironment env)
        {
            _influxDBClient = influxDBClient;
            _memoryCache = memoryCache;
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;
            _logger = logger;
            _env = env;
        }

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

                if (!_memoryCache.TryGetValue(validationCacheKey, out bool exists))
                {
                    var httpClient = _httpClientFactory.CreateClient();

                    var response = await httpClient.GetFromJsonAsync<JsonDocument>($"https://clientsettings.roblox.com/v2/settings/application/PCClientBootstrapper/bucket/{value}");
                    
                    exists = response!.RootElement.GetProperty("applicationSettings").ValueKind != JsonValueKind.Null;

                    _memoryCache.Set(validationCacheKey, exists, DateTime.Now.AddDays(1));
                }

                if (!exists)
                {
                    _logger.LogInformation("Requested nonexistent channel ({Channel})", value);
                    return BadRequest();
                }
            }

            string influxBucket = statPoint.Bucket;

            if (_env.IsDevelopment())
                influxBucket = "test-bucket";

            // TODO: batching
            // TODO: proper use of measurement property (wtf was i doing?)
            var point = PointData.Measurement("metrics")
                    .Field(key, value)
                    .Tag("version", HttpContext.Items["ClientVersion"]!.ToString())
                    .Timestamp(DateTime.UtcNow, WritePrecision.S);

            await _influxDBClient.GetWriteApiAsync().WritePointAsync(point, influxBucket);

            return Ok();
        }

        [HttpPost("post-exception")]
        public async Task<IActionResult> PostException()
        {
            using var sr = new StreamReader(Request.Body);
            string trace = await sr.ReadToEndAsync();

            if (string.IsNullOrEmpty(trace))
                return BadRequest();

            if (trace.Length >= 1024*50)
            {
                _logger.LogInformation("Exception post dropped because it was too big ({Bytes} bytes)", trace.Length);
                return BadRequest();
            }

            await _dbContext.ExceptionReports.AddAsync(new()
            {
                Timestamp = DateTime.UtcNow,
                Trace = trace
            });

            await _dbContext.SaveChangesAsync();

            return Ok();
        }
    }
}
