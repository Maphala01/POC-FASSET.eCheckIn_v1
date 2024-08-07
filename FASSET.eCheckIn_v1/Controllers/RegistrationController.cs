using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using FASSET.eCheckIn_v1.Models;

namespace FASSET.eCheckIn_v1.Controllers
{
    public class RegistrationController : Controller
    {
        Data_Access_Layer.dal _dbAccess = new Data_Access_Layer.dal();

        public RegistrationController()
        {
            _dbAccess = new Data_Access_Layer.dal();

        }

        // GET: Registration
        public ActionResult Index()
        {
            ViewBag.Departments = _dbAccess.GetDepartments();
            ViewBag.Employees = _dbAccess.GetEmployees();
            return View();
        }

        [HttpGet]
        public JsonResult GetDepartments(string term)
        {
            var departments = _dbAccess.GetDepartmentsByTerm(term);
            return Json(departments, JsonRequestBehavior.AllowGet);
        }

        [HttpGet]
        public JsonResult GetEmployees(string term)
        {
            var employees = _dbAccess.GetEmployeesByTerm(term);
            return Json(employees, JsonRequestBehavior.AllowGet);
        }

        [HttpPost]
        public ActionResult SubmitRegistration(RegistrationModel model)
        {
            model.GeoLocation = Request.Form["GeoLocation"];
            QRCodeModel qrCodeModel = new QRCodeModel();

            model.qrCodeImgUrl = qrCodeModel.GetQRCodeContent(model.GeoLocation);
            //model.QRCodeTotp = qrCodeModel.TOTP;

            //model.qrCodeImgUrl = Request.Form["qrCodeImgUrl"];
             model.QRCodeTotp = Request.Form["QRCodeTotp"];
            model.userTotp = Request.Form["userTotp"];
            model.Employee = Request.Form["Employee"];
            //model.GeoLocation = Request.Form["GeoLocation"];

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


            model.DepartmentList = _dbAccess.GetDepartments();
            model.EmployeeList = _dbAccess.GetEmployees();
            ViewBag.Departments = model.DepartmentList;
            ViewBag.Employees = model.EmployeeList;
            return View("Index", model);
        }
    }
}