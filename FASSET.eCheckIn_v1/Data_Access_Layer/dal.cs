using FASSET.eCheckIn_v1.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Data;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using System.Drawing.Drawing2D;


namespace FASSET.eCheckIn_v1.Data_Access_Layer
{
    public class dal
    {
        private readonly string _connectionString;
        SqlConnection sql_conn = new SqlConnection(ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString);
        public dal()
        {
            _connectionString = ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;
        }

        public int SaveRegistration(RegistrationModel model)
        {
            return SaveRegistration(model.Employee, model.Department, model.qrCodeImgUrl, model.QRCodeTotp, model.GeoLocation, "CheckInEmployee", "QR_Registration");
            //return SaveRegistration(model.Employee, model.Department, model.qrCodeImgUrl, model.QRCodeTotp, model.GeoLocation, "CheckInEmployee");
        }

        public int SaveRegistration(Guest_StaffModel model)
        {
            return SaveRegistration(model.Employee, model.Department, model.qrCodeImgUrl, model.QRCodeTotp, model.GeoLocation, "CheckInEmployee","Manual_Registration");
            //return SaveRegistration(model.Employee, model.Department, model.qrCodeImgUrl, model.QRCodeTotp, model.GeoLocation, "CheckInEmployee");
        }

        //private int SaveRegistration(string name, string department, string qrCodeImageUrl, string totp, string geoLocation, string trnsNm,string regType)
        private int SaveRegistration(string name, string department, string qrCodeImageUrl, string totp, string geoLocation, string trnsNm,string regType)
        {

            string empName = name;
            string empDepartment = department;
            string empQrCodeImageUrl = qrCodeImageUrl;
            string empGeoLocation = geoLocation;
            string empTOTP = "NULL";


            int res = 0;
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                SqlCommand sql_cmd = new SqlCommand("mst_spCheckInEmployee", connection);
                sql_cmd.CommandType = CommandType.StoredProcedure;
                sql_cmd.Parameters.AddWithValue("@Name", empName);
                sql_cmd.Parameters.AddWithValue("@Department", empDepartment);
                sql_cmd.Parameters.AddWithValue("@QRCodeImageUrl", empQrCodeImageUrl);
                sql_cmd.Parameters.AddWithValue("@TOTP", empTOTP);
                sql_cmd.Parameters.AddWithValue("@GeoLocation", empGeoLocation);
                sql_cmd.Parameters.AddWithValue("@RegistrationType", regType);
                sql_cmd.Parameters.AddWithValue("@TrnsNm", trnsNm);
                SqlParameter outputParam = new SqlParameter("@IsVld", SqlDbType.Int)
                {
                    Direction = ParameterDirection.Output
                };
                sql_cmd.Parameters.Add(outputParam);

                connection.Open();
                sql_cmd.ExecuteNonQuery();

                // Get the output parameter value
                res = Convert.ToInt32(outputParam.Value);
            }
            return res;
        }

        public int SaveRegistration_Guest(Guest_StaffModel model)
        {
            if (model.capacity.Equals("On my own behalf"))
            {
                string defaultCompany = "Not Applicable";
                model.Company = defaultCompany;
            }


            int res = 0;
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                SqlCommand sql_cmd = new SqlCommand("mst_spCheckInGuest", connection);
                sql_cmd.CommandType = CommandType.StoredProcedure;
                sql_cmd.Parameters.AddWithValue("@guest_capacity", model.capacity);
                sql_cmd.Parameters.AddWithValue("@guest_company", model.Company);
                sql_cmd.Parameters.AddWithValue("@guest_title", model.Title);
                sql_cmd.Parameters.AddWithValue("@guest_name", model.guestName);
                sql_cmd.Parameters.AddWithValue("@guest_email", model.guestEmail);
                sql_cmd.Parameters.AddWithValue("@guest_enquiryType", model.enquiryType);
                sql_cmd.Parameters.AddWithValue("@TrnsNm", "CheckInGuest");
                SqlParameter outputParam = new SqlParameter("@IsVld", SqlDbType.Int)
                {
                    Direction = ParameterDirection.Output
                };
                sql_cmd.Parameters.Add(outputParam);

                connection.Open();
                sql_cmd.ExecuteNonQuery();

                // Get the output parameter value
                res = Convert.ToInt32(outputParam.Value);
                return res;
            }
        }
        public List<Capacity> GetRepCapacity()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT capacityName FROM mst_representationCapacity", connection);
                var reader = command.ExecuteReader();
                var capacities = new List<Capacity>();

                while (reader.Read())
                {
                    capacities.Add(new Capacity { CapacityName = reader["CapacityName"].ToString() });
                }
                return capacities;
            }
        }

        

        public List<Title> GetTitle()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT title_type FROM mst_person_title", connection);
                var reader = command.ExecuteReader();
                var titles = new List<Title>();

                while (reader.Read())
                {
                    titles.Add(new Title { TitleName = reader["title_type"].ToString() });
                }
                return titles;
            }
        }

        public List<EnquiryType> GetEnquiryType()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT enquiry_type FROM mst_enquiry_types", connection);
                var reader = command.ExecuteReader();
                var enquiry_types = new List<EnquiryType>();

                while (reader.Read())
                {
                    enquiry_types.Add(new EnquiryType { EnquiryName = reader["enquiry_type"].ToString() });
                }
                return enquiry_types;
            }
        }


        public List<Department> GetDepartments()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT DepartmentName FROM Departments ORDER BY DepartmentName ASC", connection);
                var reader = command.ExecuteReader();

                var departments = new List<Department>();

                while (reader.Read())
                {
                    departments.Add(new Department { DepartmentName = reader["DepartmentName"].ToString() });
                }

                return departments;
            }
        }

        public List<Department_2> GetDepartments_2()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT DepartmentName FROM Departments ORDER BY DepartmentName ASC", connection);
                var reader = command.ExecuteReader();

                var departments = new List<Department_2>();

                while (reader.Read())
                {
                    departments.Add(new Department_2 { DepartmentName = reader["DepartmentName"].ToString() });
                }

                return departments;
            }
        }


        public List<Employee_2> GetEmployees_2()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT Name FROM Employees ORDER BY Name ASC", connection);
                var reader = command.ExecuteReader();

                var employees = new List<Employee_2>();

                while (reader.Read())
                {
                    employees.Add(new Employee_2 { EmployeeName = reader["Name"].ToString() });
                }

                return employees;
            }
        }
        //public List<Department> GetDepartment()
        //{
        //    using (SqlConnection connection = new SqlConnection(_connectionString))
        //    {
        //        connection.Open();
        //        var command = new SqlCommand("SELECT DepartmentName FROM Departments ORDER BY DepartmentName ASC", connection);
        //        var reader = command.ExecuteReader();

        //        var department = new List<Department>();

        //        while (reader.Read())
        //        {
        //            department.Add(new Department { DepartmentName = reader["DepartmentName"].ToString() });
        //        }

        //        return department;
        //    }
        //}

        //public List<Employee> GetEmployees()
        //{
        //    using (SqlConnection connection = new SqlConnection(_connectionString))
        //    {
        //        connection.Open();
        //        var command = new SqlCommand("SELECT Name FROM Employees ORDER BY Name ASC", connection);
        //        var reader = command.ExecuteReader();

        //        var employees = new List<Employee>();

        //        while (reader.Read())
        //        {
        //            employees.Add(new Employee { EmployeeName = reader["Name"].ToString() });
        //        }

        //        return employees;
        //    }
        //}
        //public List<Department> GetDepartments()
        //{
        //    using (SqlConnection connection = new SqlConnection(_connectionString))
        //    {
        //        connection.Open();
        //        var command = new SqlCommand("SELECT DepartmentName FROM Departments ORDER BY DepartmentName ASC", connection);
        //        var reader = command.ExecuteReader();

        //        var departments = new List<Department>();

        //        while (reader.Read())
        //        {
        //            departments.Add(new Department { DepartmentName = reader["DepartmentName"].ToString() });
        //        }

        //        return departments;
        //    }
        //}

        public List<Employee> GetEmployees()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT Name FROM Employees ORDER BY Name ASC", connection);
                var reader = command.ExecuteReader();

                var employees = new List<Employee>();

                while (reader.Read())
                {
                    employees.Add(new Employee { EmployeeName = reader["Name"].ToString() });
                }

                return employees;
            }
        }



        public List<Department> GetDepartmentsByTerm(string term)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT DepartmentName FROM Departments WHERE DepartmentName LIKE @term ORDER BY DepartmentName ASC", connection);
                command.Parameters.AddWithValue("@term", "%" + term + "%");
                var reader = command.ExecuteReader();

                var departments = new List<Department>();

                while (reader.Read())
                {
                    departments.Add(new Department { DepartmentName = reader["DepartmentName"].ToString() });
                }

                return departments;
            }
        }

        public List<Employee> GetEmployeesByTerm(string term)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT Name FROM Employees WHERE Name LIKE @term ORDER BY Name ASC", connection);
                command.Parameters.AddWithValue("@term", "%" + term + "%");
                var reader = command.ExecuteReader();

                var employees = new List<Employee>();

                while (reader.Read())
                {
                    employees.Add(new Employee { EmployeeName = reader["Name"].ToString() });
                }

                return employees;
            }
        }


    }
}