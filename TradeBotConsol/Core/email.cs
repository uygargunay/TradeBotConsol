using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;

namespace TradeBotConsol.Core
{
    internal class email
    {
        public void SendEmailNotification(string subject, string messageBody)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var fromAddress = new MailAddress("uygargunay@gmail.com", "TradeBot Live");
                var toAddress = new MailAddress("uygargunay@gmail.com");
                string fromPassword = Environment.GetEnvironmentVariable("BOT_EMAIL_PASS")?.Trim() ?? "sznd kafk nhec skqh";
                if (string.IsNullOrWhiteSpace(fromPassword))
                {
                    Console.WriteLine("[EMAIL] BOT_EMAIL_PASS is not configured; notification skipped.");
                    return;
                }

                var smtp = new SmtpClient
                {
                    Host = "smtp.gmail.com",
                    Port = 587,
                    EnableSsl = true,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(fromAddress.Address, fromPassword),
                    Timeout = 10000
                };

                using (var message = new MailMessage(fromAddress, toAddress) { Subject = subject, Body = messageBody })
                {
                    smtp.Send(message);
                }
                Console.WriteLine($"[EMAIL] Sent: {subject}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EMAIL ERROR] {ex.Message}");
            }
        }
    }
}
