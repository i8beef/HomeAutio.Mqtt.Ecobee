using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using I8Beef.Ecobee;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace HomeAutio.Mqtt.Ecobee
{
    /// <summary>
    /// Main program entry point.
    /// </summary>
    public class Program
    {
        private static StoredAuthToken _currentAuthToken;

        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <returns>Awaitable <see cref="Task" />.</returns>
        public static async Task Main()
        {
            var environmentName = Environment.GetEnvironmentVariable("ENVIRONMENT");
            if (string.IsNullOrEmpty(environmentName))
                environmentName = "Development";

            // Setup config
            var config = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
                .Build();

            // Setup logging
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(config)
                .CreateLogger();

            try
            {
                // Validates existing or gets new tokens
                await ValidateTokens(config);

                var hostBuilder = CreateHostBuilder(config);
                await hostBuilder.RunConsoleAsync();
            }
            catch (Exception ex)
            {
                Log.Logger.Fatal(ex, ex.Message);
                throw;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        /// <summary>
        /// Creates an <see cref="IHostBuilder"/>.
        /// </summary>
        /// <param name="config">External configuration.</param>
        /// <returns>A configured <see cref="IHostBuilder"/>.</returns>
        private static IHostBuilder CreateHostBuilder(IConfiguration config)
        {
            return new HostBuilder()
                .ConfigureAppConfiguration((hostContext, configuration) => configuration.AddConfiguration(config))
                .ConfigureLogging((hostingContext, logging) => logging.AddSerilog())
                .ConfigureServices((hostContext, services) =>
                {
                    // Setup client
                    services.AddScoped<Client>(serviceProvider =>
                    {
                        return new Client(
                            config.GetValue<string>("ecobee:ecobeeAppKey"),
                            ReadTokenFileAsync,
                            WriteTokenFileAsync);
                    });

                    // Setup service instance
                    services.AddScoped<IHostedService, EcobeeMqttService>(serviceProvider =>
                    {
                        var brokerSettings = new Core.BrokerSettings
                        {
                            BrokerIp = config.GetValue<string>("mqtt:brokerIp"),
                            BrokerPort = config.GetValue<int>("mqtt:brokerPort"),
                            BrokerUsername = config.GetValue<string>("mqtt:brokerUsername"),
                            BrokerPassword = config.GetValue<string>("mqtt:brokerPassword"),
                            BrokerUseTls = config.GetValue<bool>("mqtt:brokerUseTls", false)
                        };

                        // TLS settings
                        if (brokerSettings.BrokerUseTls)
                        {
                            var brokerTlsSettings = new Core.BrokerTlsSettings
                            {
                                AllowUntrustedCertificates = config.GetValue<bool>("mqtt:brokerTlsSettings:allowUntrustedCertificates", false),
                                IgnoreCertificateChainErrors = config.GetValue<bool>("mqtt:brokerTlsSettings:ignoreCertificateChainErrors", false),
                                IgnoreCertificateRevocationErrors = config.GetValue<bool>("mqtt:brokerTlsSettings:ignoreCertificateRevocationErrors", false)
                            };

                            switch (config.GetValue<string>("mqtt:brokerTlsSettings:protocol", "1.2"))
                            {
                                case "1.0":
                                    brokerTlsSettings.SslProtocol = System.Security.Authentication.SslProtocols.Tls;
                                    break;
                                case "1.1":
                                    brokerTlsSettings.SslProtocol = System.Security.Authentication.SslProtocols.Tls11;
                                    break;
                                case "1.2":
                                default:
                                    brokerTlsSettings.SslProtocol = System.Security.Authentication.SslProtocols.Tls12;
                                    break;
                            }

                            var brokerTlsCertificatesSection = config.GetSection("mqtt:brokerTlsSettings:certificates");
                            brokerTlsSettings.Certificates = brokerTlsCertificatesSection.GetChildren()
                                .Select(x =>
                                {
                                    var file = x.GetValue<string>("file");
                                    var passPhrase = x.GetValue<string>("passPhrase");

                                    if (!File.Exists(file))
                                        throw new FileNotFoundException($"Broker Certificate '{file}' is missing!");

                                    return !string.IsNullOrEmpty(passPhrase) ?
                                        new X509Certificate2(file, passPhrase) :
                                        new X509Certificate2(file);
                                }).ToList();

                            brokerSettings.BrokerTlsSettings = brokerTlsSettings;
                        }

                        return new EcobeeMqttService(
                            serviceProvider.GetRequiredService<ILogger<EcobeeMqttService>>(),
                            serviceProvider.GetRequiredService<Client>(),
                            config.GetValue<string>("ecobee:ecobeeName"),
                            config.GetValue<int>("ecobee:refreshInterval"),
                            brokerSettings);
                    });
                });
        }

        /// <summary>
        /// Write token file callback.
        /// </summary>
        /// <param name="storedAuthToken">Stored auth token.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        private static async Task WriteTokenFileAsync(StoredAuthToken storedAuthToken, CancellationToken cancellationToken = default)
        {
            // Cache the returned tokens
            _currentAuthToken = storedAuthToken;

            // Write token to persistent store
            var text = new StringBuilder();
            text.AppendLine($"{storedAuthToken.TokenExpiration:MM/dd/yy hh:mm:ss tt}");
            text.AppendLine(storedAuthToken.AccessToken);
            text.AppendLine(storedAuthToken.RefreshToken);

            await File.WriteAllTextAsync(@"token.txt", text.ToString());
        }

        /// <summary>
        /// Read token file callback.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The <see cref="StoredAuthToken"/>.</returns>
        private static async Task<StoredAuthToken> ReadTokenFileAsync(CancellationToken cancellationToken = default)
        {
            if (_currentAuthToken == null && File.Exists(@"token.txt"))
            {
                var tokenText = await File.ReadAllLinesAsync(@"token.txt");
                _currentAuthToken = new StoredAuthToken
                {
                    TokenExpiration = DateTime.Parse(tokenText[0]),
                    AccessToken = tokenText[1],
                    RefreshToken = tokenText[2]
                };
            }

            return _currentAuthToken;
        }

        /// <summary>
        /// Validates current, or gets new auth tokens.
        /// </summary>
        /// <param name="config">External configuration.</param>
        /// <returns>An awaitable <see cref="Task"/>.</returns>
        private static async Task ValidateTokens(IConfiguration config)
        {
            // initialize tokens
            var registrationClient = new Client(
                config.GetValue<string>("ecobee:ecobeeAppKey"),
                ReadTokenFileAsync,
                WriteTokenFileAsync);

            if (!File.Exists(@"token.txt") || File.ReadAllText(@"token.txt") == string.Empty)
            {
                Console.WriteLine("Getting new tokens");
                var pin = await registrationClient.GetPinAsync();

                Console.WriteLine("Pin: " + pin.EcobeePin);
                Console.WriteLine("You have " + pin.ExpiresIn + " minutes to enter this on the Ecobee site and hit enter.");

                Console.ReadLine();

                await registrationClient.GetAccessTokenAsync(pin.Code);
            }
            else
            {
                Console.WriteLine("Loading existing tokens");
                var storedAuthToken = await ReadTokenFileAsync();
            }
        }
    }
}
