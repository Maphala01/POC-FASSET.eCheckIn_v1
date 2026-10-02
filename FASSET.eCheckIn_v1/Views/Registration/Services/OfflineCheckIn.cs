using System;
using System.Globalization;

namespace FASSET.eCheckIn_v1.Services
{
    // One check-in that could not be written to the database at the moment
    // the person submitted it. Saved as a small JSON text file and replayed
    // into mst_dailyCheckIn_tbl once the database is reachable again.
    //
    // Timestamps are deliberately plain ISO-8601 STRINGS, not DateTime:
    // JavaScriptSerializer converts DateTime to/from UTC, which would
    // silently shift a SAST wall-clock time by the server's UTC offset.
    // Thrown when queued check-ins can't be synced because of a setup problem
    // (e.g. mst_spCheckInEmployee hasn't been updated to accept @CheckInTime yet)
    // rather than a problem with any one check-in. Nothing is quarantined; the
    // items stay pending and the message is shown on the Settings card.
    public class OfflineSyncSetupException : Exception
    {
        public OfflineSyncSetupException(string message) : base(message) { }
    }

    public class OfflineCheckIn
    {
        public string QueueId { get; set; }
        public string Employee { get; set; }
        public string Department { get; set; }
        public string QrCodeImageUrl { get; set; }
        public string QrCodeTotp { get; set; }
        public string GeoLocation { get; set; }
        public string RegistrationType { get; set; }
        public string TransactionName { get; set; }

        // When the person actually pressed Check In (SAST). This - not the
        // time of the later replay - is what ends up in dateCreated.
        public string CheckInTimeSast { get; set; }
        public string QueuedAtSast { get; set; }

        public int Attempts { get; set; }
        public string LastError { get; set; }
        public string LastAttemptSast { get; set; }

        // Filled in when the item leaves the pending folder.
        public string ResolvedAtSast { get; set; }
        public string ResolutionNote { get; set; }

        public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fff";

        public static string Format(DateTime value)
        {
            return value.ToString(TimeFormat, CultureInfo.InvariantCulture);
        }

        public DateTime GetCheckInTime()
        {
            return DateTime.ParseExact(CheckInTimeSast, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None);
        }
    }
}
