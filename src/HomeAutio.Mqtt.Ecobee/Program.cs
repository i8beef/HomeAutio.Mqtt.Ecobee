using I8Beef.Ecobee;
using I8Beef.Ecobee.Messages;
using NLog;
using System;
using System.Configuration;
using System.IO;
using System.Text;
using Topshelf;

namespace HomeAutio.Mqtt.Ecobee
{
    class Program
    {
        static void Main(string[] args)
        {
            var log = LogManager.GetCurrentClassLogger();

            var brokerIp = ConfigurationManager.AppSettings["brokerIp"];
            var brokerPort = int.Parse(ConfigurationManager.AppSettings["brokerPort"]);
            var brokerUsername = ConfigurationManager.AppSettings["brokerUsername"];
            var brokerPassword = ConfigurationManager.AppSettings["brokerPassword"];

            var ecobeeName = ConfigurationManager.AppSettings["ecobeeName"];
            var ecobeeAppKey = ConfigurationManager.AppSettings["ecobeeAppKey"];

            // Default to once every 3 minutes as per API guide
            int ecobeeRereshInterval = 240000;
            if (int.TryParse(ConfigurationManager.AppSettings["ecobeeRefreshInterval"], out ecobeeRereshInterval))
                ecobeeRereshInterval = Math.Max(240000, ecobeeRereshInterval * 1000);

            if (args.Length > 0 && args[0] == "auth")
            {
                Console.WriteLine("Getting new tokens");
                var pin = Client.GetPin(ecobeeAppKey).Result;

                Console.WriteLine("PIN: " + pin.EcobeePin);
                Console.WriteLine("You have " + pin.ExpiresIn + " minutes to enter this on the Ecobee site.");
                Console.WriteLine("Once you have verified this PIN on the Ecobee site, you may hit enter to retrieve and cache the auth token.");
                Console.WriteLine("Then you may install / start the service normally.");

                Console.ReadKey();

                var authToken = Client.GetAccessToken(ecobeeAppKey, pin.Code).Result;
                WriteTokenFile(authToken);
            }
            else
            {
                if (!File.Exists(@"token.txt"))
                {
                    log.Error("Token file token.txt not found");
                    Console.WriteLine("Token file token.txt not found. Please run  'HomeAutio.Mqtt.Ecobee.exe auth' to retrieve and cache new auth token.");
                    Console.ReadKey();

                    return;
                }

                log.Info("Reading cached tokens");
                var tokenText = File.ReadAllLines(@"token.txt");

                var tokenExpiration = DateTime.Parse(tokenText[0]);
                var accessToken = tokenText[1];
                var refreshToken = tokenText[2];

                log.Info("Access Token: " + accessToken);
                log.Info("Refresh Token: " + refreshToken);

                var client = new Client(ecobeeAppKey, accessToken, refreshToken, tokenExpiration);
                client.AuthTokenUpdated += (o, e) => { WriteTokenFile(e); };

                HostFactory.Run(x =>
                {
                    x.UseNLog();

                    x.Service<EcobeeMqttService>(s =>
                    {
                        s.ConstructUsing(name => new EcobeeMqttService(client, ecobeeName, ecobeeRereshInterval, brokerIp, brokerPort, brokerUsername, brokerPassword));
                        s.WhenStarted(tc => tc.Start());
                        s.WhenStopped(tc => tc.Stop());
                    });

                    x.EnableServiceRecovery(r =>
                    {
                        r.RestartService(0);
                        r.RestartService(0);
                        r.RestartService(0);
                    });

                    x.RunAsLocalSystem();
                    x.UseAssemblyInfoForServiceInfo();
                });
            }
        }

        /// <summary>
        /// Writes the auth token to the token cache file.
        /// </summary>
        /// <param name="token">The token to cache.</param>
        public static void WriteTokenFile(AuthToken token)
        {
            var text = new StringBuilder();
            text.AppendLine(DateTime.Now.AddSeconds(token.ExpiresIn).ToString());
            text.AppendLine(token.AccessToken);
            text.AppendLine(token.RefreshToken);

            // Cache the returned tokens
            File.WriteAllText(@"token.txt", text.ToString());
        }
    }
}
