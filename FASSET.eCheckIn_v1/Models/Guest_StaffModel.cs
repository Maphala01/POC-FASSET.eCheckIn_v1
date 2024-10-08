﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Web;
using FASSET.eCheckIn_v1.Models;

namespace FASSET.eCheckIn_v1.Models
{
    public class Guest_StaffModel
    {
        //--------------------Staff--------------------------------------//
        public int SelectedDepartmentId { get; set; }
        public string Department { get; set; }
        public string Employee { get; set; }
        public string GeoLocation { get; set; }
        public string qrCodeImgUrl { get; set; }
        public string QRCodeTotp { get; set; }
        public List<Department_2> DepartmentList { get; set; } = new List<Department_2>();
        public List<Employee_2> EmployeeList { get; set; } = new List<Employee_2>();
        //--------------------Staff--------------------------------------//
        public string capacity { get; set; }
        public string Company { get; set; }
        public string Title { get; set; }
        public string guestName { get; set; }
        public string guestEmail { get; set; }
        public string enquiryType { get; set; }

        public List<Capacity> CapacityList { get; set; } = new List<Capacity>();
        public List<Title> TitleList { get; set; } = new List<Title>();
        public List<EnquiryType> EnquiryTypeList { get; set; } = new List<EnquiryType>();

    }

    public class Department_2
    {
        public string DepartmentName { get; set; }
    }
    public class Employee_2
    {
        public string EmployeeName { get; set; }
    }

    public class Capacity
    {
        public string CapacityName { get; set; }
    }

    public class Title
    { 
        public string TitleName { get; set; }
    }

    public class EnquiryType
    { 
        public string EnquiryName { get; set; }
    }
}