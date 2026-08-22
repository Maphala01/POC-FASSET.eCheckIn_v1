using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using OfficeOpenXml;

namespace FASSET.eCheckIn_v1.Services
{
    public class ParsedScheduleRow
    {
        public string HostLocation { get; set; }
        public string EmployeeNameRaw { get; set; }
        public DateTime ScheduleDate { get; set; }
        public int? SeatSlot { get; set; }
        public bool IsVacant { get; set; }
        public bool IsPublicHoliday { get; set; }
    }

    public static class ScheduleCalendarParser
    {
        private static readonly string[] Months = {
            "January","February","March","April","May","June","July",
            "August","September","October","November","December"
        };

        private static readonly Regex YearRegex = new Regex(@"(20\d{2})");
        private static readonly Regex SeatRegex = new Regex(@"^S(\d+):\s*(.+)$");

        public static List<ParsedScheduleRow> Parse(System.IO.Stream fileStream, string hostLocation)
        {
            var rows = new List<ParsedScheduleRow>();

            using (var package = new ExcelPackage(fileStream))
            {
                foreach (var worksheet in package.Workbook.Worksheets)
                {
                    int monthIndex = Array.IndexOf(Months, worksheet.Name);
                    if (monthIndex < 0) continue; // skip Summary / Groups / anything non-month
                    if (worksheet.Dimension == null) continue; // empty sheet

                    string titleText = worksheet.Cells[1, 1].Text;
                    var yearMatch = YearRegex.Match(titleText ?? "");
                    if (!yearMatch.Success)
                        throw new Exception($"Could not find a year in title on sheet '{worksheet.Name}': {titleText}");
                    int year = int.Parse(yearMatch.Value);
                    int month = monthIndex + 1;

                    int lastRow = worksheet.Dimension.End.Row;
                    int lastCol = worksheet.Dimension.End.Column;

                    for (int r = 3; r <= lastRow; r++) // row 3 onward — rows 1-2 are title/day headers
                    {
                        for (int c = 1; c <= lastCol; c++)
                        {
                            string raw = worksheet.Cells[r, c].Text;
                            if (string.IsNullOrWhiteSpace(raw)) continue;

                            var lines = raw.Split('\n');
                            string dayLine = lines[0].Trim();
                            int day;
                            if (!int.TryParse(dayLine, out day)) continue;

                            DateTime theDate;
                            try { theDate = new DateTime(year, month, day); }
                            catch { continue; }

                            for (int i = 1; i < lines.Length; i++)
                            {
                                string line = lines[i].Trim();
                                if (line.Length == 0) continue;

                                if (line.Equals("PUBLIC HOLIDAY", StringComparison.OrdinalIgnoreCase))
                                {
                                    rows.Add(new ParsedScheduleRow
                                    {
                                        HostLocation = hostLocation,
                                        EmployeeNameRaw = null,
                                        ScheduleDate = theDate,
                                        SeatSlot = null,
                                        IsVacant = false,
                                        IsPublicHoliday = true,
                                    });
                                    continue;
                                }

                                var seatMatch = SeatRegex.Match(line);
                                if (!seatMatch.Success) continue;

                                int seat = int.Parse(seatMatch.Groups[1].Value);
                                string name = seatMatch.Groups[2].Value.Trim();
                                bool isVacant = name.Equals("Vacant Seat", StringComparison.OrdinalIgnoreCase);

                                rows.Add(new ParsedScheduleRow
                                {
                                    HostLocation = hostLocation,
                                    EmployeeNameRaw = isVacant ? null : name,
                                    ScheduleDate = theDate,
                                    SeatSlot = seat,
                                    IsVacant = isVacant,
                                    IsPublicHoliday = false,
                                });
                            }
                        }
                    }
                }
            }

            return rows;
        }
    }
}