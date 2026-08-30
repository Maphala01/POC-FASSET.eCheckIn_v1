using FASSET.eCheckIn_v1.Models;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Mvc;


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
        }

        public int SaveRegistration(Guest_StaffModel model)
        {
            return SaveRegistration(model.Employee, model.Department, model.qrCodeImgUrl, model.QRCodeTotp, model.GeoLocation, "CheckInEmployee", "Manual_Registration");
        }

        private int SaveRegistration(string name, string department, string qrCodeImageUrl, string totp, string geoLocation, string trnsNm, string regType)
        {

            string empName = name;
            string empDepartment = department;
            string empQrCodeImageUrl = qrCodeImageUrl;
            string empGeoLocation = geoLocation;
            string empTOTP = "NULL";

            // South Africa has no daylight saving, so UTC+2 is always
            // correct - this is used both for the duplicate-check-in
            // guard below and for correcting the timestamp after insert,
            // so neither depends on the SQL Server machine's own clock
            // or timezone configuration (which is what caused the
            // 2-hour-off timestamps - GETDATE() inside the stored
            // procedure returns whatever timezone the server OS is set
            // to, not necessarily SAST).
            DateTime sastNow;
            try
            {
                var sastZone = TimeZoneInfo.FindSystemTimeZoneById("South Africa Standard Time");
                sastNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, sastZone);
            }
            catch (TimeZoneNotFoundException)
            {
                // Fallback if this Windows timezone ID isn't registered on
                // this particular server - SAST is always exactly UTC+2.
                sastNow = DateTime.UtcNow.AddHours(2);
            }

            int res = 0;
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();

                // Guard against duplicate check-ins for the same person on
                // the same (SAST) day - checked BEFORE calling the stored
                // procedure at all, so a second/third submission never
                // reaches the INSERT in the first place, regardless of
                // whatever duplicate-detection logic (or lack of it) exists
                // inside mst_spCheckInEmployee itself.
                int? employeeId = ResolveEmployeeIdByName(empName);
                if (employeeId.HasValue)
                {
                    var dupCheckCmd = new SqlCommand(
                        "SELECT COUNT(1) FROM [dbo].[mst_dailyCheckIn_tbl] WHERE Name = @EmployeeId AND CAST(dateCreated AS DATE) = @Today",
                        connection);
                    dupCheckCmd.Parameters.AddWithValue("@EmployeeId", employeeId.Value);
                    dupCheckCmd.Parameters.AddWithValue("@Today", sastNow.Date);

                    int alreadyCheckedIn = Convert.ToInt32(dupCheckCmd.ExecuteScalar());
                    if (alreadyCheckedIn > 0)
                    {
                        return 0; // "You are checked in already!" - same code the controller already handles
                    }
                }

                SqlCommand sql_cmd = new SqlCommand("mst_spCheckInEmployee", connection);
                sql_cmd.CommandType = CommandType.StoredProcedure;

                sql_cmd.Parameters.AddWithValue("@Name", empName);
                sql_cmd.Parameters.AddWithValue("@Department", empDepartment);
                sql_cmd.Parameters.AddWithValue("@QRCodeImageUrl", empQrCodeImageUrl);
                sql_cmd.Parameters.AddWithValue("@RegistrationType", regType);
                sql_cmd.Parameters.AddWithValue("@TrnsNm", trnsNm);
                sql_cmd.Parameters.AddWithValue("@GeoLocation", empGeoLocation);

                // 🔧 Explicit OUTPUT parameter
                SqlParameter outputParam = new SqlParameter("@IsVld", SqlDbType.Int)
                {
                    Direction = ParameterDirection.Output
                };
                sql_cmd.Parameters.Add(outputParam);

                sql_cmd.ExecuteNonQuery();

                res = Convert.ToInt32(outputParam.Value);

                // Correct the just-inserted row's timestamp to real SAST
                // time, rather than trusting whatever GETDATE() returned
                // inside the stored procedure. Best-effort: if this fails
                // for any reason, the check-in itself still succeeded, so
                // it's wrapped separately and never surfaces as an error
                // to the person checking in.
                if (res == 99)
                {
                    try
                    {
                        var identityCmd = new SqlCommand("SELECT CAST(SCOPE_IDENTITY() AS INT)", connection);
                        var newIdObj = identityCmd.ExecuteScalar();
                        if (newIdObj != null && newIdObj != DBNull.Value)
                        {
                            var fixTimestampCmd = new SqlCommand(
                                "UPDATE [dbo].[mst_dailyCheckIn_tbl] SET dateCreated = @DateCreated WHERE Id = @Id",
                                connection);
                            fixTimestampCmd.Parameters.AddWithValue("@DateCreated", sastNow);
                            fixTimestampCmd.Parameters.AddWithValue("@Id", Convert.ToInt32(newIdObj));
                            fixTimestampCmd.ExecuteNonQuery();
                        }
                    }
                    catch
                    {
                        // Timestamp correction is best-effort - never let it
                        // fail a check-in that already succeeded.
                    }
                }
            }
            return res;
        }


        // NOTE: intentionally NOT filtered to IsActive=1 - this feeds
        // schedule-vs-actual reporting math (expected days, By Month's
        // Planned Seat-Days, etc.) for a specific historical date range.
        // An employee who has since gone inactive but was scheduled during
        // that range should still show up correctly in those numbers.
        public List<ScheduledDayRow> GetScheduledDays(DateTime startDate, DateTime endDateExclusive, string department)
        {
            var rows = new List<ScheduledDayRow>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                    SELECT
                        s.EmployeeId, e.Name AS EmployeeName, s.ScheduleDate, s.HostLocation
                    FROM [dbo].Schedules s 
                    JOIN [dbo].[Employees] e ON e.Id = s.EmployeeId
                    WHERE s.ScheduleDate >= @StartDate AND s.ScheduleDate < @EndDateExclusive AND s.IsVacant = 0 AND s.IsPublicHoliday = 0 AND (@Department IS NULL OR e.DepartmentName = @Department)";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@StartDate", startDate.Date);
                command.Parameters.AddWithValue("@EndDateExclusive", endDateExclusive.Date);
                command.Parameters.AddWithValue("@Department", (object)department ?? DBNull.Value);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ScheduledDayRow
                    {
                        EmployeeId = Convert.ToInt32(reader["EmployeeId"]),
                        EmployeeName = reader["EmployeeName"] as string,
                        ScheduleDate = Convert.ToDateTime(reader["ScheduleDate"]),
                        HostLocation = reader["HostLocation"] as string
                    });
                }
            }

            return rows;
        }

        public List<string> GetDistinctRawGeoLocations()
        {
            var locations = new List<string>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "SELECT DISTINCT GeoLocation FROM [dbo].[mst_dailyCheckIn_tbl] WHERE GeoLocation IS NOT NULL",
                    connection);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    locations.Add(reader["GeoLocation"].ToString());
                }
            }

            return locations;
        }


        public List<SiteInfo> GetSites()
        {
            var sites = new List<SiteInfo>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "SELECT SiteName, Latitude, Longitude, RadiusMeters FROM [dbo].[mst_Sites]",
                    connection);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    sites.Add(new SiteInfo
                    {
                        SiteName = reader["SiteName"].ToString(),
                        Latitude = Convert.ToDouble(reader["Latitude"]),
                        Longitude = Convert.ToDouble(reader["Longitude"]),
                        RadiusMeters = Convert.ToInt32(reader["RadiusMeters"])
                    });
                }
            }

            return sites;
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

        // NOTE: intentionally NOT filtered to IsActive=1 - this is historical
        // check-in report data for a specific date range. An employee who
        // has since gone inactive still genuinely checked in on those past
        // dates, and that shouldn't disappear from the report.
        public List<CheckInReportRow> GetCheckInReportData(DateTime startDate, DateTime endDate, string department)
        {
            var rows = new List<CheckInReportRow>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                    SELECT
                        c.Id,
                        c.dateCreated,
                        e.Name AS EmployeeName,
                        e.DepartmentName,
                        e.Gender,
                        e.Ethnicity,
                        e.Occupational_Level,
                        e.Position,
                        c.GeoLocation
                    FROM [dbo].[mst_dailyCheckIn_tbl] c
                    JOIN [dbo].[Employees] e ON c.Name = e.Id
                    WHERE c.dateCreated >= @StartDate
                      AND c.dateCreated < @EndDateExclusive
                      AND (@Department IS NULL OR e.DepartmentName = @Department)
                    ORDER BY c.dateCreated ASC;";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@StartDate", startDate.Date);
                // Use an exclusive upper bound (end date + 1 day) so the
                // whole end day is included regardless of time-of-day values.
                command.Parameters.AddWithValue("@EndDateExclusive", endDate.Date.AddDays(1));
                command.Parameters.AddWithValue("@Department", (object)department ?? DBNull.Value);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new CheckInReportRow
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        DateCreated = Convert.ToDateTime(reader["dateCreated"]),
                        EmployeeName = reader["EmployeeName"] as string,
                        DepartmentName = reader["DepartmentName"] as string,
                        Gender = reader["Gender"] as string,
                        Ethnicity = reader["Ethnicity"] as string,
                        OccupationalLevel = reader["Occupational_Level"] as string,
                        Position = reader["Position"] as string,
                        GeoLocation = reader["GeoLocation"] as string
                    });
                }
            }

            return rows;
        }

        // Distinct department names for the report's filter dropdown —
        // pulled from Employees (not the Departments table) since that's
        // what GetCheckInReportData actually groups/filters on.
        // NOTE: intentionally NOT filtered to IsActive=1 - if an inactive
        // employee's department has real historical check-in data, that
        // department should still be selectable in the report's filter.
        public List<string> GetDepartmentNamesForReporting()
        {
            var departments = new List<string>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "SELECT DISTINCT DepartmentName FROM [dbo].[Employees] WHERE DepartmentName IS NOT NULL ORDER BY DepartmentName ASC",
                    connection);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    departments.Add(reader["DepartmentName"].ToString());
                }
            }

            return departments;
        }




        public List<Department_2> GetDepartments_2()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT DISTINCT DepartmentName FROM Departments ORDER BY DepartmentName ASC", connection);
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
                var command = new SqlCommand("SELECT DISTINCT Name FROM Employees WHERE IsActive = 1 ORDER BY Name ASC", connection);
                var reader = command.ExecuteReader();

                var employees = new List<Employee_2>();

                while (reader.Read())
                {
                    employees.Add(new Employee_2 { EmployeeName = reader["Name"].ToString() });
                }

                return employees;
            }
        }

        // NOTE: intentionally NOT filtered to IsActive=1 - "last check-in"
        // is inherently historical (and for someone who went inactive,
        // knowing when they last checked in is exactly the useful part).
        public Dictionary<string, DateTime> GetLastCheckInPerEmployee(string department)
        {
            var result = new Dictionary<string, DateTime>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                    SELECT e.Name AS EmployeeName, MAX(c.dateCreated) AS LastCheckIn
                    FROM [dbo].[Employees] e
                    LEFT JOIN [dbo].[mst_dailyCheckIn_tbl] c ON c.Name = e.Id
                    WHERE (@Department IS NULL OR e.DepartmentName = @Department)
                    GROUP BY e.Name";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@Department", (object)department ?? DBNull.Value);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader["EmployeeName"].ToString();
                    if (reader["LastCheckIn"] != DBNull.Value)
                    {
                        result[name] = Convert.ToDateTime(reader["LastCheckIn"]);
                    }
                    else
                    {
                        // Never checked in at all — represent as DateTime.MinValue
                        // so it sorts to the top of "most overdue".
                        result[name] = DateTime.MinValue;
                    }
                }
            }

            return result;
        }

        // Filtered to IsActive=1: this headcount is the denominator for
        // "Attendance Rate by Department" in the reports - someone marked
        // inactive shouldn't keep dragging that rate down forever.
        public Dictionary<string, int> GetEmployeeHeadcountByDepartment()
        {
            var result = new Dictionary<string, int>();

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    @"SELECT DepartmentName, COUNT(*) AS Headcount
                      FROM [dbo].[Employees]
                      WHERE DepartmentName IS NOT NULL AND IsActive = 1
                      GROUP BY DepartmentName",
                    connection);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result[reader["DepartmentName"].ToString()] = Convert.ToInt32(reader["Headcount"]);
                }
            }

            return result;
        }


        public List<Employee> GetEmployees()
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand("SELECT Name FROM Employees WHERE IsActive = 1 ORDER BY Name ASC", connection);
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
                var command = new SqlCommand("SELECT Name FROM Employees WHERE Name LIKE @term AND IsActive = 1 ORDER BY Name ASC", connection);
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

        public int BulkInsertSchedules(List<ScheduleModel> rows)
        {
                var table = new DataTable();
                table.Columns.Add("HostLocation", typeof(string));
                table.Columns.Add("EmployeeId", typeof(int));
                table.Columns.Add("EmployeeNameRaw", typeof(string));
                table.Columns.Add("ScheduleDate", typeof(DateTime));
                table.Columns.Add("SeatSlot", typeof(int));
                table.Columns.Add("IsVacant", typeof(bool));
                table.Columns.Add("IsPublicHoliday", typeof(bool));
                table.Columns.Add("CreatedBy", typeof(string));
        
                foreach (var r in rows)
                {
                    var dr = table.NewRow();
                    dr["HostLocation"] = r.HostLocation;
                    dr["EmployeeId"] = (object)r.EmployeeId ?? DBNull.Value;
                    dr["EmployeeNameRaw"] = r.EmployeeNameRaw ?? "";
                    dr["ScheduleDate"] = r.ScheduleDate;
                    dr["SeatSlot"] = (object)r.SeatSlot ?? DBNull.Value;
                    dr["IsVacant"] = r.IsVacant;
                    dr["IsPublicHoliday"] = r.IsPublicHoliday;
                    dr["CreatedBy"] = r.CreatedBy ?? "Import";
                    table.Rows.Add(dr);
                }
        
                using (SqlConnection connection = new SqlConnection(_connectionString))
                {
                    connection.Open();
                    using (var bulkCopy = new SqlBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = "dbo.Schedules";
                        bulkCopy.ColumnMappings.Add("HostLocation", "HostLocation");
                        bulkCopy.ColumnMappings.Add("EmployeeId", "EmployeeId");
                        bulkCopy.ColumnMappings.Add("EmployeeNameRaw", "EmployeeNameRaw");
                        bulkCopy.ColumnMappings.Add("ScheduleDate", "ScheduleDate");
                        bulkCopy.ColumnMappings.Add("SeatSlot", "SeatSlot");
                        bulkCopy.ColumnMappings.Add("IsVacant", "IsVacant");
                        bulkCopy.ColumnMappings.Add("IsPublicHoliday", "IsPublicHoliday");
                        bulkCopy.ColumnMappings.Add("CreatedBy", "CreatedBy");
                        bulkCopy.WriteToServer(table);
                    }
                }
                return rows.Count;
            }
    
        // Best-effort case-insensitive name match against Employees.Name.
        // Returns null (unmatched) rather than guessing on a partial match.
        // Filtered to IsActive=1: a newly-imported schedule shouldn't
        // silently attach itself to someone who's been marked inactive -
        // they'll instead fall through to the "unmatched name" list, which
        // is the correct, visible outcome for a schedule importer to flag.
        public int? ResolveEmployeeIdByName(string name)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                var command = new SqlCommand(
                    "SELECT Id FROM [dbo].[Employees] WHERE LOWER(Name) = LOWER(@Name) AND IsActive = 1", connection);
                command.Parameters.AddWithValue("@Name", name);
                var result = command.ExecuteScalar();
                return result == null ? (int?)null : Convert.ToInt32(result);
            }
        }
    
        public List<ScheduleModel> GetScheduleForDate(DateTime date, string hostLocation = null)
        {
            var rows = new List<ScheduleModel>();
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                SELECT Id, HostLocation, EmployeeId, EmployeeNameRaw, ScheduleDate,
                       SeatSlot, IsVacant, IsPublicHoliday, CreatedAt, CreatedBy
                FROM [dbo].[Schedules]
                WHERE ScheduleDate = @Date
                  AND (@HostLocation IS NULL OR HostLocation = @HostLocation)
                ORDER BY HostLocation, SeatSlot;";
                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@Date", date.Date);
                command.Parameters.AddWithValue("@HostLocation", (object)hostLocation ?? DBNull.Value);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ScheduleModel
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        HostLocation = reader["HostLocation"] as string,
                        EmployeeId = reader["EmployeeId"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["EmployeeId"]),
                        EmployeeNameRaw = reader["EmployeeNameRaw"] as string,
                        ScheduleDate = Convert.ToDateTime(reader["ScheduleDate"]),
                        SeatSlot = reader["SeatSlot"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["SeatSlot"]),
                        IsVacant = Convert.ToBoolean(reader["IsVacant"]),
                        IsPublicHoliday = Convert.ToBoolean(reader["IsPublicHoliday"]),
                        CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
                        CreatedBy = reader["CreatedBy"] as string,
                    });
                }
            }
            return rows;
        }


        public List<string> GetDistinctHostLocations()
        {
            var result = new List<string>();
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "SELECT DISTINCT HostLocation FROM [dbo].[Schedules] WHERE HostLocation IS NOT NULL ORDER BY HostLocation ASC",
                    connection);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(reader["HostLocation"].ToString());
                }
            }
            return result;
        }

        // Filtered to IsActive=1: this is the "add employee to schedule"
        // picker - shouldn't be able to newly assign an inactive employee
        // to a seat going forward.
        public List<EmployeeOption> GetEmployeesForDropdown()
        {
            var result = new List<EmployeeOption>();
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand("SELECT Id, Name FROM [dbo].[Employees] WHERE IsActive = 1 ORDER BY Name ASC", connection);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new EmployeeOption
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        Name = reader["Name"].ToString()
                    });
                }
            }
            return result;
        }

        public List<ScheduleModel> GetScheduleForMonth(int year, int month, string hostLocation)
        {
            var rows = new List<ScheduleModel>();
            var startDate = new DateTime(year, month, 1);
            var endDateExclusive = startDate.AddMonths(1);

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                // De-duplicate: if a site's file was imported more than once, the same
                // HostLocation+Date+Seat can end up with several rows. Only the most
                // recently inserted row per seat/day is shown, so a re-import "wins"
                // instead of stacking duplicates on the calendar.
                string sql = @"
                    ;WITH Ranked AS (
                        SELECT *,
                               ROW_NUMBER() OVER (
                                   PARTITION BY HostLocation, ScheduleDate, SeatSlot
                                   ORDER BY Id DESC
                               ) AS rn
                        FROM [dbo].[Schedules]
                        WHERE ScheduleDate >= @StartDate AND ScheduleDate < @EndDateExclusive
                          AND (@HostLocation IS NULL OR HostLocation = @HostLocation)
                    )
                    SELECT Id, HostLocation, EmployeeId, EmployeeNameRaw, ScheduleDate,
                           SeatSlot, IsVacant, IsPublicHoliday, CreatedAt, CreatedBy
                    FROM Ranked
                    WHERE rn = 1
                    ORDER BY ScheduleDate, SeatSlot;";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@StartDate", startDate);
                command.Parameters.AddWithValue("@EndDateExclusive", endDateExclusive);
                command.Parameters.AddWithValue("@HostLocation", (object)hostLocation ?? DBNull.Value);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ScheduleModel
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        HostLocation = reader["HostLocation"] as string,
                        EmployeeId = reader["EmployeeId"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["EmployeeId"]),
                        EmployeeNameRaw = reader["EmployeeNameRaw"] as string,
                        ScheduleDate = Convert.ToDateTime(reader["ScheduleDate"]),
                        SeatSlot = reader["SeatSlot"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["SeatSlot"]),
                        IsVacant = Convert.ToBoolean(reader["IsVacant"]),
                        IsPublicHoliday = Convert.ToBoolean(reader["IsPublicHoliday"]),
                        CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
                        CreatedBy = reader["CreatedBy"] as string,
                    });
                }
            }
            return rows;
        }

        public ScheduleModel GetScheduleById(int id)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                    SELECT Id, HostLocation, EmployeeId, EmployeeNameRaw, ScheduleDate,
                           SeatSlot, IsVacant, IsPublicHoliday, CreatedAt, CreatedBy
                    FROM [dbo].[Schedules]
                    WHERE Id = @Id;";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@Id", id);

                connection.Open();
                var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    return new ScheduleModel
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        HostLocation = reader["HostLocation"] as string,
                        EmployeeId = reader["EmployeeId"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["EmployeeId"]),
                        EmployeeNameRaw = reader["EmployeeNameRaw"] as string,
                        ScheduleDate = Convert.ToDateTime(reader["ScheduleDate"]),
                        SeatSlot = reader["SeatSlot"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["SeatSlot"]),
                        IsVacant = Convert.ToBoolean(reader["IsVacant"]),
                        IsPublicHoliday = Convert.ToBoolean(reader["IsPublicHoliday"]),
                        CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
                        CreatedBy = reader["CreatedBy"] as string,
                    };
                }
                return null;
            }
        }

        public int InsertSchedule(ScheduleModel model)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                    INSERT INTO [dbo].[Schedules]
                        (HostLocation, EmployeeId, EmployeeNameRaw, ScheduleDate, SeatSlot, IsVacant, IsPublicHoliday, CreatedAt, CreatedBy)
                    OUTPUT INSERTED.Id
                    VALUES
                        (@HostLocation, @EmployeeId, @EmployeeNameRaw, @ScheduleDate, @SeatSlot, @IsVacant, @IsPublicHoliday, GETDATE(), @CreatedBy);";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@HostLocation", model.HostLocation);
                command.Parameters.AddWithValue("@EmployeeId", (object)model.EmployeeId ?? DBNull.Value);
                command.Parameters.AddWithValue("@EmployeeNameRaw", model.EmployeeNameRaw ?? "");
                command.Parameters.AddWithValue("@ScheduleDate", model.ScheduleDate.Date);
                command.Parameters.AddWithValue("@SeatSlot", (object)model.SeatSlot ?? DBNull.Value);
                command.Parameters.AddWithValue("@IsVacant", model.IsVacant);
                command.Parameters.AddWithValue("@IsPublicHoliday", model.IsPublicHoliday);
                command.Parameters.AddWithValue("@CreatedBy", model.CreatedBy ?? "Admin");

                connection.Open();
                var newId = command.ExecuteScalar();
                return Convert.ToInt32(newId);
            }
        }

        public bool UpdateSchedule(ScheduleModel model)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                    UPDATE [dbo].[Schedules]
                    SET HostLocation = @HostLocation,
                        EmployeeId = @EmployeeId,
                        EmployeeNameRaw = @EmployeeNameRaw,
                        ScheduleDate = @ScheduleDate,
                        SeatSlot = @SeatSlot,
                        IsVacant = @IsVacant,
                        IsPublicHoliday = @IsPublicHoliday
                    WHERE Id = @Id;";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@Id", model.Id);
                command.Parameters.AddWithValue("@HostLocation", model.HostLocation);
                command.Parameters.AddWithValue("@EmployeeId", (object)model.EmployeeId ?? DBNull.Value);
                command.Parameters.AddWithValue("@EmployeeNameRaw", model.EmployeeNameRaw ?? "");
                command.Parameters.AddWithValue("@ScheduleDate", model.ScheduleDate.Date);
                command.Parameters.AddWithValue("@SeatSlot", (object)model.SeatSlot ?? DBNull.Value);
                command.Parameters.AddWithValue("@IsVacant", model.IsVacant);
                command.Parameters.AddWithValue("@IsPublicHoliday", model.IsPublicHoliday);

                connection.Open();
                int affected = command.ExecuteNonQuery();
                return affected > 0;
            }
        }

        public bool DeleteSchedule(int id)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand("DELETE FROM [dbo].[Schedules] WHERE Id = @Id;", connection);
                command.Parameters.AddWithValue("@Id", id);

                connection.Open();
                int affected = command.ExecuteNonQuery();
                return affected > 0;
            }
        }


        // Same de-duplication logic as GetScheduleForMonth, but takes an
        // arbitrary date range — FullCalendar's month view actually spans
        // parts of the previous/next month too, so it asks for a range,
        // not a clean calendar month.
        public List<ScheduleModel> GetScheduleForDateRange(DateTime start, DateTime end, string hostLocation)
        {
            var rows = new List<ScheduleModel>();
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                string sql = @"
                    ;WITH Ranked AS (
                        SELECT *,
                               ROW_NUMBER() OVER (
                                   PARTITION BY HostLocation, ScheduleDate, SeatSlot
                                   ORDER BY Id DESC
                               ) AS rn
                        FROM [dbo].[Schedules]
                        WHERE ScheduleDate >= @StartDate AND ScheduleDate < @EndDate
                          AND (@HostLocation IS NULL OR HostLocation = @HostLocation)
                    )
                    SELECT Id, HostLocation, EmployeeId, EmployeeNameRaw, ScheduleDate,
                           SeatSlot, IsVacant, IsPublicHoliday, CreatedAt, CreatedBy
                    FROM Ranked
                    WHERE rn = 1
                    ORDER BY ScheduleDate, SeatSlot;";

                var command = new SqlCommand(sql, connection);
                command.Parameters.AddWithValue("@StartDate", start.Date);
                command.Parameters.AddWithValue("@EndDate", end.Date);
                command.Parameters.AddWithValue("@HostLocation", (object)hostLocation ?? DBNull.Value);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new ScheduleModel
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        HostLocation = reader["HostLocation"] as string,
                        EmployeeId = reader["EmployeeId"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["EmployeeId"]),
                        EmployeeNameRaw = reader["EmployeeNameRaw"] as string,
                        ScheduleDate = Convert.ToDateTime(reader["ScheduleDate"]),
                        SeatSlot = reader["SeatSlot"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["SeatSlot"]),
                        IsVacant = Convert.ToBoolean(reader["IsVacant"]),
                        IsPublicHoliday = Convert.ToBoolean(reader["IsPublicHoliday"]),
                        CreatedAt = Convert.ToDateTime(reader["CreatedAt"]),
                        CreatedBy = reader["CreatedBy"] as string,
                    });
                }
            }
            return rows;
        }

        // Picks the next free seat number for a site+day so two people
        // dropped on the same day never collide with each other (and never
        // collide with the null-seat de-dup logic above — a NULL SeatSlot
        // would make every drag-dropped entry on the same day look like
        // "the same seat" to the ranking query, silently hiding all but one).
        public int GetNextAvailableSeatSlot(string hostLocation, DateTime date)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "SELECT ISNULL(MAX(SeatSlot), 0) + 1 FROM [dbo].[Schedules] WHERE HostLocation = @HostLocation AND ScheduleDate = @Date",
                    connection);
                command.Parameters.AddWithValue("@HostLocation", hostLocation);
                command.Parameters.AddWithValue("@Date", date.Date);

                connection.Open();
                var result = command.ExecuteScalar();
                return (result == null || result == DBNull.Value) ? 1 : Convert.ToInt32(result);
            }
        }

        // Employees who've actually appeared in Schedules for this site —
        // not the full global Employees table. Keeps the "add employee"
        // picker scoped to people who plausibly belong at this location.
        // Filtered to IsActive=1 as well: no reason to offer re-assigning
        // an inactive employee to a new seat.
        public List<EmployeeOption> GetEmployeesForHostLocation(string hostLocation)
        {
            var result = new List<EmployeeOption>();
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    @"SELECT DISTINCT e.Id, e.Name
                      FROM [dbo].[Schedules] s
                      JOIN [dbo].[Employees] e ON e.Id = s.EmployeeId
                      WHERE s.HostLocation = @HostLocation AND s.EmployeeId IS NOT NULL AND e.IsActive = 1
                      ORDER BY e.Name ASC",
                    connection);
                command.Parameters.AddWithValue("@HostLocation", hostLocation);

                connection.Open();
                var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new EmployeeOption
                    {
                        Id = Convert.ToInt32(reader["Id"]),
                        Name = reader["Name"].ToString()
                    });
                }
            }
            return result;
        }

        public string GetAdminPasswordHash(string username)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "SELECT PasswordHash FROM [dbo].[Admins] WHERE Username = @Username",
                    connection);
                command.Parameters.AddWithValue("@Username", username);

                connection.Open();
                var result = command.ExecuteScalar();
                return (result == null || result == DBNull.Value) ? null : result.ToString();
            }
        }

        public bool AdminUsernameExists(string username)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "SELECT COUNT(1) FROM [dbo].[Admins] WHERE Username = @Username",
                    connection);
                command.Parameters.AddWithValue("@Username", username);

                connection.Open();
                return Convert.ToInt32(command.ExecuteScalar()) > 0;
            }
        }

        public void InsertAdmin(string username, string passwordHash)
        {
            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand(
                    "INSERT INTO [dbo].[Admins] (Username, PasswordHash) VALUES (@Username, @PasswordHash)",
                    connection);
                command.Parameters.AddWithValue("@Username", username);
                command.Parameters.AddWithValue("@PasswordHash", passwordHash);

                connection.Open();
                command.ExecuteNonQuery();
            }
        }
    }
}
