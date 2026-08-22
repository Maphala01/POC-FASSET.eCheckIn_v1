using System;

namespace FASSET.eCheckIn_v1.Models
{
    public class ScheduleModel
    {
        public int Id { get; set; }
        public string HostLocation { get; set; }
        public int? EmployeeId { get; set; }
        public string EmployeeNameRaw { get; set; }
        public DateTime ScheduleDate { get; set; }
        public int? SeatSlot { get; set; }
        public bool IsVacant { get; set; }
        public bool IsPublicHoliday { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; }
    }
}