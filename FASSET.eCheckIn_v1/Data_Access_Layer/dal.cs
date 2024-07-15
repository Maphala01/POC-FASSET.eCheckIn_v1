using FASSET.eCheckIn_v1.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Data;
using System.Linq;
using System.Web;

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
            int res = 0;
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {


                SqlCommand sql_cmd = new SqlCommand("mst_spCheckInEmployee", sql_conn);
                sql_cmd.CommandType = CommandType.StoredProcedure;
                sql_cmd.Parameters.AddWithValue("@Name", model.Employee);
                sql_cmd.Parameters.AddWithValue("@Department", model.Department);
                sql_cmd.Parameters.AddWithValue("@QRCodeImageUrl", model.qrCodeImgUrl);
                sql_cmd.Parameters.AddWithValue("@TOTP", model.QRCodeTotp);
                sql_cmd.Parameters.AddWithValue("@UserTOTP", model.userTotp);
                sql_cmd.Parameters.AddWithValue("@GeoLocation", model.GeoLocation);
                sql_cmd.Parameters.AddWithValue("@TrnsNm", "CheckInEmployee");
                SqlParameter outputParam = new SqlParameter("@IsVld", SqlDbType.Int)
                {
                    Direction = ParameterDirection.Output
                };
                sql_cmd.Parameters.Add(outputParam);

                sql_conn.Open();
                sql_cmd.ExecuteNonQuery();

                // Get the output parameter value
                res = Convert.ToInt32(outputParam.Value);
            }

            return res;

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