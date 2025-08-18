namespace Bloxstrap.Services.Data.Entities
{
    public class ExceptionReport
    {
        public int Id { get; set; }

        public DateTime Timestamp { get; set; }

        public required string Trace { get; set; }
    }
}
