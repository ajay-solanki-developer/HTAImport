namespace HTADataImport.Models
{
    public class HTARecord
    {
        // Customer Information
        public string? IntakeDate { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Address { get; set; }
        public string? City { get; set; }
        public string? Prov { get; set; }
        public string? Postal { get; set; }
        public string? YnDiscrete { get; set; }
        public string? HomePhone { get; set; }
        public string? BusinessPhone { get; set; }
        public string? Ext { get; set; }
        public string? Cell { get; set; }
        public string? Fax { get; set; }
        public string? Gender { get; set; }
        public string? Notes { get; set; }
        public string? Language { get; set; }
        
        // Ticket Information
        public string? POT { get; set; }
        public string? ICON { get; set; }
        public string? TicketDate { get; set; }
        public string? Intake { get; set; }
        public string? SectionNumber { get; set; }
        public string? OffenseWording { get; set; }
        public string? SpeedingGoing { get; set; }
        public string? SpeedingInA { get; set; }
        public string? BadgeNumber { get; set; }
        public string? CourtName { get; set; }
        public string? FirstApp { get; set; }
        public string? Rm { get; set; }
        public string? Time { get; set; }
        public string? Disposition { get; set; }
        public string? Name { get; set; }
        public string? SpecialInstructions { get; set; }
        public string? TicketNotes { get; set; }
        public string? DateDisclosureRequested { get; set; }
        public string? DateDisclosureReceived { get; set; }
        
        // Financial Information
        public string? Guarantee { get; set; }
        public string? WePay { get; set; }
        public string? Fee { get; set; }
        public string? Fine { get; set; }  // HePays in source table
        public string? Tax { get; set; }   // GST in source table
        public string? Total { get; set; }
        public string? Paid { get; set; }  // TotalPayments in source table
        public string? Balance { get; set; }
    }
}