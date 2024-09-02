using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Models;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class Staff_GuestController : Controller
    {
        Data_Access_Layer.dal _dbAccess = new Data_Access_Layer.dal();

        public Staff_GuestController()
        {
            _dbAccess = new Data_Access_Layer.dal();

        }
        // GET: Staff_Guest
        public ActionResult Index()
        {
            ViewBag.Departments = _dbAccess.GetDepartments_2();
            ViewBag.Employees = _dbAccess.GetEmployees_2();
            ViewBag.Capacities = _dbAccess.GetRepCapacity();
            ViewBag.Titles = _dbAccess.GetTitle();
            ViewBag.Enquiries = _dbAccess.GetEnquiryType();

            return View();
        }

        public ActionResult SubmitRegistration_Guest(Guest_StaffModel model)
        {
            int res = _dbAccess.SaveRegistration_Guest(model);
            if (res == 99)
            {
                DateTime today = DateTime.Today;
                string dayOfWeek = today.DayOfWeek.ToString();
                string message = model.guestName + $" you're checked in...Happy {dayOfWeek} !";
                TempData["CheckIn-Success"] = message;
                return RedirectToAction("Index");

            }
            model.DepartmentList = _dbAccess.GetDepartments_2();
            model.EmployeeList = _dbAccess.GetEmployees_2();
            model.CapacityList = _dbAccess.GetRepCapacity();
            model.TitleList = _dbAccess.GetTitle();

            ViewBag.Departments = model.DepartmentList;
            ViewBag.Employees = model.EmployeeList;
            ViewBag.Capacities = model.CapacityList;
            ViewBag.Titles = model.TitleList;
            return View("Index", model);
        }

            [HttpPost]
        public ActionResult SubmitRegistration(Guest_StaffModel model)
        {
            QRCodeModel qrCodeModel = new QRCodeModel();
            model.GeoLocation = Request.Form["GeoLocation"];
            Console.WriteLine("Received GeoLocation: " + model.GeoLocation);
            model.qrCodeImgUrl = qrCodeModel.GetQRCodeContent(model.GeoLocation);

            int res = _dbAccess.SaveRegistration(model);


            if (res == 99)
            {
                DateTime today = DateTime.Today;
                string dayOfWeek = today.DayOfWeek.ToString();
                string message = model.Employee + $" you're checked in...Happy {dayOfWeek} !";
                TempData["CheckIn-Success"] = message;
                //ViewBag.Message = message;
                //ViewBag.MessageType = "success";
                return RedirectToAction("Index");

            }
            else if (res == 0)
            {
                ViewBag.Message = "You are checked in already!";
                ViewBag.MessageType = "error";
            }

            else if(res == -1)
            {
                ViewBag.Message = "User does not exist!";
                ViewBag.MessageType = "error";
            }

            else if (res == -2)
            {
                ViewBag.Message = "Department does not exist!";
                ViewBag.MessageType = "error";
            }
            else if (res == -3)
            {
                ViewBag.Message = "Your checkIn was unsuccessful..contact ICT!";
                ViewBag.MessageType = "error";
            }
 
                model.DepartmentList = _dbAccess.GetDepartments_2();
                model.EmployeeList = _dbAccess.GetEmployees_2();
                model.CapacityList = _dbAccess.GetRepCapacity();
                model.TitleList = _dbAccess.GetTitle();

                ViewBag.Departments = model.DepartmentList;
                ViewBag.Employees = model.EmployeeList;
                ViewBag.Capacities = model.CapacityList;
                ViewBag.Titles = model.TitleList;
                return View("Index", model);
            
            
        }
    }
}