using System;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using static QRCoder.PayloadGenerator;

namespace FASSET.eCheckIn_v1.Models
{

    public class QRCodeModel
    {
        public string QRCodeImageUrl { get; set; }
        public string TOTP { get; private set; } // Added TOTP property

        public string GetQRCodeContent(string geoLocation)
        {
            // Generates a unique URL with expiration time
            var expirationTime = DateTime.UtcNow.AddSeconds(30); // QR code expires in 30 seconds
            var timestamp = expirationTime.ToString("yyyy-MM-dd-HH-mm-ss");
            TOTP = GenerateTOTP(); // Generate and set the TOTP
                                   // var url = $"http://172.26.160.1%3A8077?timestamp={timestamp}&otp={TOTP}";web-echeckin.azurewebsites.net
                                   //var url = "https://localhost:44302/Registration/Index";
            //var url = $"https://localhost:44302/Registration/Index?timestamp={timestamp}&geolocation={geoLocation}";
            var url = $"https://webecheckin.azurewebsites.net/Registration/Index?timestamp={timestamp}&geolocation={geoLocation}";
            return url;
        }

        private string GenerateTOTP()
        {
            using (var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("your-very-secret-key")))
            {
                var timeStep = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
                var timeStepBytes = BitConverter.GetBytes(timeStep);
                if (BitConverter.IsLittleEndian)
                {
                    Array.Reverse(timeStepBytes);
                }

                var hash = hmac.ComputeHash(timeStepBytes);
                var offset = hash[hash.Length - 1] & 0x0F;
                var binaryCode = (hash[offset] & 0x7F) << 24
                               | (hash[offset + 1] & 0xFF) << 16
                               | (hash[offset + 2] & 0xFF) << 8
                               | (hash[offset + 3] & 0xFF);

                var totp = binaryCode % 100000;
                return totp.ToString("D5");
            }
        }
    }
}