namespace Bloxstrap.Services.Models
{
    public class StatPoint
    {
        public required string Name { get; set; }

        public List<string>? Values { get; set; }

        public string Bucket { get; set; } = "bloxstrap";

        public string BucketPublic { get; set; } = "bloxstrap";
    }
}
