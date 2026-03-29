using CsvHelper.Configuration;

namespace HTADataImport.Models
{
    public class HTARecordMap : ClassMap<HTARecord>
    {
        public HTARecordMap()
        {
            Map(m => m.IntakeDate).Name("IntakeDate");
            Map(m => m.FirstName).Name("First Name");
            Map(m => m.LastName).Name("Lastname");
            Map(m => m.Address).Name("Address");
            Map(m => m.City).Name("City");
            Map(m => m.Prov).Name("Prov");
            Map(m => m.Postal).Name("Postal");
            Map(m => m.YnDiscrete).Name("ynDiscrete");
            Map(m => m.HomePhone).Name("homephone");
            Map(m => m.BusinessPhone).Name("businessphone");
            Map(m => m.Ext).Name("Ext");
            Map(m => m.Cell).Name("Cell");
            Map(m => m.Fax).Name("Fax");
            Map(m => m.Gender).Name("Gender");
            Map(m => m.Notes).Name("tblClient_Notes");
            Map(m => m.POT).Name("POT");
            Map(m => m.ICON).Name("ICON");
            Map(m => m.TicketDate).Name("TicketDate");
            Map(m => m.Intake).Name("Intake");
            Map(m => m.SectionNumber).Name("SectionNumber");
            Map(m => m.OffenseWording).Name("tblOffenseWording_Description");
        }
    }
}