using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace FASSET.eCheckIn_v1.Models
{
    public class Guest_StaffModel
    {
        public int guestID { get; set; }
        public string guestName { get; set; }
        public string guestAffiliationType { get; set; }
        public string guestAffiliationDetails { get; set; }
        public string guestEmails { get; set; }
        public string contactPerson { get; set; }
        public string guest_contactPerson_Email { get; set; }
        public int staffId { get; set; }
        public string staffName { get; set; }
        public string staffDepartement { get; set; }


    }
}