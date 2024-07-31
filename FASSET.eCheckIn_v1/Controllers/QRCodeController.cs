using FASSET.eCheckIn_v1.Models;
using System;
using System.Collections.Generic;
using System.Drawing; // This imports System.Drawing.Point and System.Drawing.Size
using System.Drawing.Imaging;
using System.Linq;
using System.Web;
using QRCoder;
using System.Web.Mvc;
using System.IO;

namespace FASSET.eCheckIn_v1.Controllers
{
    [NoCache]
    [OutputCache(Duration = 8, VaryByParam = "none", NoStore = true)]
    public class QRCodeController : Controller
    {
        // GET: QRCode
        [NoCache]
        [OutputCache(Duration = 8, VaryByParam = "none", NoStore = true)] // Added OutputCache attribute
        public ActionResult Index(string geoLocation)
        {
            var model = new QRCodeModel();
            var qrCodeContent = model.GetQRCodeContent(geoLocation);

            // Check if the QR code is expired or TOTP is invalid
            var expirationTimestamp = Request.QueryString["timestamp"];
            var otp = Request.QueryString["otp"];
            if (!string.IsNullOrEmpty(expirationTimestamp) && !string.IsNullOrEmpty(otp))
            {
                var expirationTime = DateTime.ParseExact(expirationTimestamp, "yyyy-MM-dd-HH-mm-ss", null);
                if (DateTime.UtcNow > expirationTime || otp != model.TOTP)
                {
                    // QR code expired or invalid OTP, return error view or handle as needed
                    ViewBag.QRCodeExpired = true;
                    return View(model);
                }
            }
            try
            {
                using (var qrGenerator = new QRCoder.QRCodeGenerator()) // Corrected the usage of QRCodeGenerator here
                using (var qrCodeData = qrGenerator.CreateQrCode(qrCodeContent, QRCoder.QRCodeGenerator.ECCLevel.Q))
                using (var qrCode = new QRCoder.QRCode(qrCodeData))
                using (var bitmap = qrCode.GetGraphic(20))
                {
                    // Load the logo image
                    var logoPath = Server.MapPath("~/Content/download.jpg");
                    using (var logo = Image.FromFile(logoPath))
                    {
                        var combinedImage = AddLogoToQRCode(bitmap, logo);

                        string fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}.png";
                        string filePath = Server.MapPath($"~/Content/QRCodeImages/{fileName}");
                        // Ensure the directory exists and has write permissions
                        Directory.CreateDirectory(Server.MapPath("~/Content/QRCodeImages"));
                        combinedImage.Save(filePath, ImageFormat.Png);

                        model.QRCodeImageUrl = Url.Content($"~/Content/QRCodeImages/{fileName}");

                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error generating or saving QR code: " + ex.Message);
            }

            TempData["QRCodeImageUrl"] = model.QRCodeImageUrl;
            TempData["TOTP"] = model.TOTP;
            return View(model);
        }

        private Bitmap AddLogoToQRCode(Bitmap qrCodeImage, Image logo)
        {
            int logoSize = qrCodeImage.Width / 5; // Adjust size as needed
            var logoPosition = new System.Drawing.Point((qrCodeImage.Width - logoSize) / 2, (qrCodeImage.Height - logoSize) / 2); // Fully qualify Point

            var combinedImage = new Bitmap(qrCodeImage.Width, qrCodeImage.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(combinedImage))
            {
                graphics.DrawImage(qrCodeImage, new System.Drawing.Point(0, 0)); // Fully qualify Point
                graphics.DrawImage(logo, new Rectangle(logoPosition, new System.Drawing.Size(logoSize, logoSize))); // Fully qualify Size
            }

            return combinedImage;
        }
    }
}
