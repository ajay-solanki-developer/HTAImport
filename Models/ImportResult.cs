namespace HTADataImport.Models
{
    public class ImportResult
    {
        public int CustomersImported { get; set; }
        public int TicketsImported { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}