using FASSET.eCheckIn_v1.Models;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Web.Mvc;
using QRCoder;

namespace FASSET.eCheckIn_v1.Controllers
{
    [NoCache]
    public class QRCodeController : Controller
    {
        // GET: QRCode
        [NoCache]
        [OutputCache(Duration = 0, VaryByParam = "none", NoStore = true)]
        public ActionResult Index(string geoLocation)
        {
            var model = new QRCodeModel();
            var qrCodeContent = model.GetQRCodeContent(geoLocation);

            var expirationTimestamp = Request.QueryString["timestamp"];
            var otp = Request.QueryString["otp"];
            if (!string.IsNullOrEmpty(expirationTimestamp) && !string.IsNullOrEmpty(otp))
            {
                var expirationTime = DateTime.ParseExact(expirationTimestamp, "yyyy-MM-dd-HH-mm-ss", null);
                if (DateTime.UtcNow > expirationTime || otp != model.TOTP)
                {
                    ViewBag.QRCodeExpired = true;
                    return View(model);
                }
            }

            try
            {
                string outputDir = Server.MapPath("~/Content/QRCodeImages/");
                Directory.CreateDirectory(outputDir);

                using (var qrGenerator = new QRCodeGenerator())
                using (var qrCodeData = qrGenerator.CreateQrCode(qrCodeContent, QRCodeGenerator.ECCLevel.Q))
                using (var qrCode = new QRCode(qrCodeData))
                using (var bitmap = qrCode.GetGraphic(20))
                {
                    string fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}.png";
                    string filePath = Path.Combine(outputDir, fileName);

                    string logoPath = Server.MapPath("~/Content/FASSET_HD.jpg");
                    if (System.IO.File.Exists(logoPath))
                    {
                        using (var logo = Image.FromFile(logoPath))
                        using (var combinedImage = AddLogoToQRCode(bitmap, logo))
                        {
                            combinedImage.Save(filePath, ImageFormat.Png);
                        }
                    }
                    else
                    {
                        bitmap.Save(filePath, ImageFormat.Png);
                    }

                    model.QRCodeImageUrl = Url.Content($"~/Content/QRCodeImages/{fileName}");
                }
            }
            catch (Exception ex)
            {
                ViewBag.QRError = "QR generation failed: " + ex.Message;
                System.Diagnostics.Debug.WriteLine("QR generation error: " + ex);
            }

            TempData["QRCodeImageUrl"] = model.QRCodeImageUrl;
            TempData["TOTP"] = model.TOTP;

            Response.Cache.SetCacheability(System.Web.HttpCacheability.NoCache);
            Response.Cache.SetNoStore();

            return View(model);
        }

        private Bitmap AddLogoToQRCode(Bitmap qrCodeImage, Image logo)
        {
            int logoSize = qrCodeImage.Width / 5;
            var logoPosition = new Point((qrCodeImage.Width - logoSize) / 2, (qrCodeImage.Height - logoSize) / 2);

            var combinedImage = new Bitmap(qrCodeImage.Width, qrCodeImage.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(combinedImage))
            {
                graphics.DrawImage(qrCodeImage, new Point(0, 0));
                graphics.DrawImage(logo, new Rectangle(logoPosition, new Size(logoSize, logoSize)));
            }

            return combinedImage;
        }
    }
}