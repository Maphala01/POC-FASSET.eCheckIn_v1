using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace FASSET.eCheckIn_v1.Models
{
    public class DayRosterRowInput
    {
        public int Id { get; set; }
        public int? EmployeeId { get; set; }
        public bool IsVacant { get; set; }
    }
}