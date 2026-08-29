using System;
using System.Collections.Generic;

namespace FASSET.eCheckIn_v1.Models
{
    public class ScheduleCalendarViewModel
    {
        public string SelectedHostLocation { get; set; }
        public int Year { get; set; }
        public int Month { get; set; }
        public List<string> HostLocations { get; set; } = new List<string>();
        public List<EmployeeOption> Employees { get; set; } = new List<EmployeeOption>();

        // Kept for the read-only grid view — the drag-and-drop Edit page doesn't use this.
        public Dictionary<DateTime, List<ScheduleModel>> EntriesByDate { get; set; } = new Dictionary<DateTime, List<ScheduleModel>>();
    }
}