using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Web;

namespace FASSET.eCheckIn_v1.Models
{
    public class RegistrationModel
    {
        public int SelectedDepartmentId { get; set; }
        public int DepartmentID { get; set; }
        public string DepartmentName { get; set; }
        [Required]
        public string empName { get; set; }
        public string userTotp { get; set; }
        public string empDepartment { get; set; }
        public string QRCodeTotp { get; set; }
        public string qrCodeImgUrl { get; set; }
        public string Department { get; set; }
        public string Employee { get; set; }
        public List<Department> DepartmentList { get; set; } = new List<Department>();
        public List<Employee> EmployeeList { get; set; } = new List<Employee>();
        public string GeoLocation { get; set; }
    }

    public class Department
    {
        public string DepartmentName { get; set; }
    }
    public class Employee
    {
        public string EmployeeName { get; set; }
    }
}