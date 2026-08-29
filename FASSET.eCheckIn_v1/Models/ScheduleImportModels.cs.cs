using System;
using System.Collections.Generic;

namespace FASSET.eCheckIn_v1.Models
{
    public class ScheduleImportResultRow
    {
        public string HostLocation { get; set; }
        public string FileName { get; set; }
        public int RowsInserted { get; set; }
        public int VacantSeats { get; set; }
        public int PublicHolidayDays { get; set; }
        public List<string> UnmatchedNames { get; set; } = new List<string>();
        public string Error { get; set; }
    }

    public class ScheduleImportViewModel
    {
        public List<ScheduleImportResultRow> Results { get; set; } = new List<ScheduleImportResultRow>();
    }
}