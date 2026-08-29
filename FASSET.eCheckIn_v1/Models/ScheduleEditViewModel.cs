using System.Collections.Generic;

namespace FASSET.eCheckIn_v1.Models
{
    public class ScheduleEditViewModel
    {
        public ScheduleModel Entry { get; set; } = new ScheduleModel();
        public List<string> HostLocations { get; set; } = new List<string>();
        public List<EmployeeOption> Employees { get; set; } = new List<EmployeeOption>();

        // Where "Cancel" / "Save" / "Delete" should send the user back to.
        public int ReturnYear { get; set; }
        public int ReturnMonth { get; set; }
        public string ReturnHostLocation { get; set; }
    }
}
