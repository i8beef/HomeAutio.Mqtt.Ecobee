using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HomeAutio.Mqtt.Core;
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
        private static StoredAuthToken? _currentAuthToken;

        /// <summary>
        /// Main program entry point.
        /// </summary>
        /// <returns>Awaitable <see cref="Task" />.</returns>
        public static async Task Main()
        {
            var environmentName = Environment.GetEnvironmentVariable("ENVIRONMENT");
            if (string.IsNullOrEmpty(environmentName))
            {
                environmentName = "Development";
            }

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
                    services.AddScoped(serviceProvider => new Client(
                        config.GetValue<string>("ecobee:ecobeeAppKey"),
                        ReadTokenFileAsync,
                        WriteTokenFileAsync));

                    // Setup service instance
                    services.AddScoped<IHostedService, EcobeeMqttService>(serviceProvider =>
                    {
                        // TLS settings
                        var brokerUseTls = config.GetValue("mqtt:brokerUseTls", false);
                        BrokerTlsSettings? brokerTlsSettings = null;
                        if (brokerUseTls)
                        {
                            var sslProtocol = config.GetValue("mqtt:brokerTlsSettings:protocol", "1.2") switch
                            {
                                "1.2" => System.Security.Authentication.SslProtocols.Tls12,
                                "1.3" => System.Security.Authentication.SslProtocols.Tls13,
                                _ => throw new NotSupportedException($"Only TLS 1.2 and 1.3 are supported")
                            };

                            var brokerTlsCertificatesSection = config.GetSection("mqtt:brokerTlsSettings:certificates");
                            var brokerTlsCertificates = brokerTlsCertificatesSection.GetChildren()
                                .Select(x =>
                                {
                                    var file = x.GetValue<string>("file");
                                    var passPhrase = x.GetValue<string>("passPhrase");

                                    if (!File.Exists(file))
                                    {
                                        throw new FileNotFoundException($"Broker Certificate '{file}' is missing!");
                                    }

                                    return !string.IsNullOrEmpty(passPhrase) ?
                                        new X509Certificate2(file, passPhrase) :
                                        new X509Certificate2(file);
                                }).ToList();

                            brokerTlsSettings = new BrokerTlsSettings
                            {
                                AllowUntrustedCertificates = config.GetValue("mqtt:brokerTlsSettings:allowUntrustedCertificates", false),
                                IgnoreCertificateChainErrors = config.GetValue("mqtt:brokerTlsSettings:ignoreCertificateChainErrors", false),
                                IgnoreCertificateRevocationErrors = config.GetValue("mqtt:brokerTlsSettings:ignoreCertificateRevocationErrors", false),
                                SslProtocol = sslProtocol,
                                Certificates = brokerTlsCertificates
                            };
                        }

                        var brokerSettings = new BrokerSettings
                        {
                            BrokerIp = config.GetValue<string>("mqtt:brokerIp") ?? throw new InvalidOperationException("Configuration value mqtt:brokerIp not found"),
                            BrokerPort = config.GetValue("mqtt:brokerPort", 1883),
                            BrokerUsername = config.GetValue<string>("mqtt:brokerUsername"),
                            BrokerPassword = config.GetValue<string>("mqtt:brokerPassword"),
                            BrokerUseTls = brokerUseTls,
                            BrokerTlsSettings = brokerTlsSettings
                        };

                        return new EcobeeMqttService(
                            serviceProvider.GetRequiredService<ILogger<EcobeeMqttService>>(),
                            serviceProvider.GetRequiredService<Client>(),
                            config.GetValue<string>("ecobee:ecobeeName") ?? "default",
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

            await File.WriteAllTextAsync(@"token.txt", text.ToString(), cancellationToken);
        }

        /// <summary>
        /// Read token file callback.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The <see cref="StoredAuthToken"/>.</returns>
        private static async Task<StoredAuthToken?> ReadTokenFileAsync(CancellationToken cancellationToken = default)
        {
            if (_currentAuthToken is null && File.Exists(@"token.txt"))
            {
                var tokenText = await File.ReadAllLinesAsync(@"token.txt", cancellationToken);
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
