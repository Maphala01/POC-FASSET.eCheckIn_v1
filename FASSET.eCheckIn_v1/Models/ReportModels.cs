using System;
using System.Collections.Generic;

namespace FASSET.eCheckIn_v1.Models
{
    public class CheckInReportRow
    {
        public int Id { get; set; }
        public DateTime DateCreated { get; set; }
        public string EmployeeName { get; set; }
        public string DepartmentName { get; set; }
        public string Gender { get; set; }
        public string Ethnicity { get; set; }
        public string OccupationalLevel { get; set; }
        public string Position { get; set; }
        public string GeoLocation { get; set; }
    }

    public class ReportFilters
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string Department { get; set; }
    }
}
