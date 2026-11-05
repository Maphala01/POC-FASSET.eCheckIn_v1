using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;

namespace FASSET.eCheckIn_v1.Services
{
    // The two attendance rules agreed at the e-Check-in review (A3, A4),
    // kept in one place so every report and page uses the same numbers.
    public static class AttendanceRules
    {
        private static readonly TimeSpan DefaultLateCutoff = new TimeSpan(8, 30, 0);

        // A4: a check-in AFTER this time of day counts as late. Set by
        // <add key="LateCutoff" value="08:30" /> in Web.config appSettings;
        // change it there when HR confirms the code of conduct time - no
        // code change or rebuild needed (an app pool recycle picks it up).
        public static TimeSpan LateCutoff
        {
            get
            {
                TimeSpan value;
                string configured = ConfigurationManager.AppSettings["LateCutoff"];
                if (!string.IsNullOrWhiteSpace(configured)
                    && TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out value)
                    && value > TimeSpan.Zero && value < TimeSpan.FromDays(1))
                {
                    return value;
                }
                return DefaultLateCutoff;
            }
        }

        public static string LateCutoffLabel
        {
            get { return LateCutoff.ToString(@"hh\:mm"); }
        }

        // Uses the time a report page asks for (?lateAfter=HH:mm) when it's
        // valid, otherwise the configured cut-off.
        public static TimeSpan ResolveLateCutoff(string requested)
        {
            TimeSpan value;
            if (!string.IsNullOrWhiteSpace(requested)
                && TimeSpan.TryParse(requested, CultureInfo.InvariantCulture, out value)
                && value > TimeSpan.Zero && value < TimeSpan.FromDays(1))
            {
                return value;
            }
            return LateCutoff;
        }

        public static bool IsLate(DateTime checkInTime)
        {
            return checkInTime.TimeOfDay > LateCutoff;
        }

        public static DateTime StartOfWeek(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.Date.AddDays(-diff);
        }

        // A3: missed check-ins are based on actual weekly presence, not on
        // the exact scheduled dates. For each Monday-Sunday week:
        //   required = days the person was scheduled that week
        //   present  = distinct days they actually checked in that week
        //   missed   = max(0, required - present)
        // So a seat swap (scheduled Tuesday, came in Thursday instead) is
        // NOT a miss, while coming in fewer days than scheduled is.
        // Days present beyond what was scheduled don't carry over to other
        // weeks.
        public static WeeklyAttendance Calculate(IEnumerable<DateTime> scheduledDates, IEnumerable<DateTime> checkInTimes)
        {
            var scheduledByWeek = scheduledDates
                .Select(d => d.Date).Distinct()
                .GroupBy(StartOfWeek)
                .ToDictionary(g => g.Key, g => g.Count());

            var presentByWeek = checkInTimes
                .Select(d => d.Date).Distinct()
                .GroupBy(StartOfWeek)
                .ToDictionary(g => g.Key, g => g.Count());

            var weeks = scheduledByWeek.Keys.Union(presentByWeek.Keys)
                .OrderBy(w => w)
                .Select(w =>
                {
                    int required = scheduledByWeek.ContainsKey(w) ? scheduledByWeek[w] : 0;
                    int present = presentByWeek.ContainsKey(w) ? presentByWeek[w] : 0;
                    return new WeekAttendance
                    {
                        WeekStart = w,
                        RequiredDays = required,
                        PresentDays = present,
                        MissedDays = Math.Max(0, required - present)
                    };
                })
                .ToList();

            return new WeeklyAttendance { Weeks = weeks };
        }
    }

    public class WeekAttendance
    {
        public DateTime WeekStart { get; set; }
        public int RequiredDays { get; set; }
        public int PresentDays { get; set; }
        public int MissedDays { get; set; }

        // Present days that count towards what was required that week.
        public int CreditedDays { get { return Math.Min(RequiredDays, PresentDays); } }

        public string Label
        {
            get { return "Week of " + WeekStart.ToString("d MMMM", CultureInfo.InvariantCulture); }
        }
    }

    public class WeeklyAttendance
    {
        public List<WeekAttendance> Weeks { get; set; }

        public int RequiredDays { get { return Weeks.Sum(w => w.RequiredDays); } }
        public int PresentDays { get { return Weeks.Sum(w => w.PresentDays); } }
        public int CreditedDays { get { return Weeks.Sum(w => w.CreditedDays); } }
        public int MissedDays { get { return Weeks.Sum(w => w.MissedDays); } }

        // Share of required days met, capped at 100%. Null when nothing was
        // scheduled in the range ("Not scheduled").
        public double? RatePercent
        {
            get
            {
                return RequiredDays == 0
                    ? (double?)null
                    : Math.Round(100.0 * CreditedDays / RequiredDays, 1);
            }
        }

        public List<WeekAttendance> WeeksShort
        {
            get { return Weeks.Where(w => w.MissedDays > 0).ToList(); }
        }
    }
}
