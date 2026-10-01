// Add this as its own class in Models/EmployeeDetail.cs, alongside your
// other model classes (CheckInReportRow, ScheduledDayRow, SiteInfo, etc.)
// - NOT nested inside dal.cs.

namespace FASSET.eCheckIn_v1.Models
{
    public class EmployeeDetail
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string DepartmentName { get; set; }
        public bool IsActive { get; set; }
        public string Gender { get; set; }
        public string Ethnicity { get; set; }
        public string OccupationalLevel { get; set; }
        public string Position { get; set; }
    }
}