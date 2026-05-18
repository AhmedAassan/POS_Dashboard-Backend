using System;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Hosting;

namespace PosDashboard.Administration
{
    public class FakeSMSService : ISMSService
    {
        private IWebHostEnvironment HostEnvironment { get; }

        public FakeSMSService(IWebHostEnvironment hostEnvironment)
        {
            HostEnvironment = hostEnvironment ??
                throw new ArgumentNullException(nameof(hostEnvironment));
        }

        public void Send(string phoneNumber, string text, string reason)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                throw new ArgumentNullException(nameof(phoneNumber));

            var path = Path.Combine(HostEnvironment.ContentRootPath, "App_Data", "SMS");
            Directory.CreateDirectory(path);

            var fileName = Path.Combine(path, phoneNumber + ".txt");
            File.AppendAllLines(fileName, new string[]
            {
                DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture) + ": " +
                text,
                ""
            });
        }
    }
}